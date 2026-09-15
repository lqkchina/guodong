namespace JellyWallpaper.Core.Physics;

/// <summary>
/// 二维弹簧-质点网格（Spring-Mass Grid）物理引擎 —— 果冻弹性形变的核心。
///
/// ── 模型描述 ─────────────────────────────────────────────────────────
/// 把壁纸图像所在平面离散成一个 N 行 × M 列的质点网格：
///
///     ●──●──●──●
///     │╲ │╲ │╲ │     每个 ● 是一个 MassPoint（质量 m 归一化为 1），
///     ●──●──●──●     相邻质点之间用"弹簧"连接（横向 + 纵向，
///     │╲ │╲ │╲ │     不建斜向弹簧以控制计算量；需要更"黏"的手感可扩展）。
///     ●──●──●──●
///
/// 每个质点同时受四类力作用（详见 Step() 中的力学公式注释）：
///   ① 弹簧力   —— 相邻质点的拉伸/压缩产生的弹性回复力（胡克定律）；
///   ② 锚点力   —— 指向"原始坐标"的弱弹簧，保证松手后网格整体复位；
///   ③ 粘性阻尼 —— 正比于速度、方向相反的耗散力，让震荡逐渐停止；
///   ④ 拖拽外力 —— 鼠标按下时，影响半径内的质点被拉向鼠标。
///
/// ── 时间积分 ─────────────────────────────────────────────────────────
/// 使用"半隐式欧拉法"（semi-implicit Euler，又称 symplectic Euler）：
///     v(t+dt) = v(t) + a(t)·dt      （先更新速度）
///     x(t+dt) = x(t) + v(t+dt)·dt   （再用"新"速度更新位置）
/// 相比显式欧拉（先用旧速度更新位置）更稳定、能量耗散更小，是弹簧质点系统
/// 的常用选择。固定步长 dt = 1/60 s，由 SimulationLoop 保证与渲染帧率解耦。
///
/// ── 稳定性条件 ───────────────────────────────────────────────────────
/// 显式/半隐式欧拉对弹簧系统的稳定性要求近似满足：
///         k · dt² / m < 2   （k 为弹簧刚度，m 为质点质量）
/// 默认 k = 120、m = 1、dt = 1/60 → k·dt² ≈ 0.033 ≪ 2，稳定且有余量。
/// 若把 Stiffness 调到极大（> 7200），系统可能发散，UI 建议值上限已做约束。
///
/// ── 渲染线程解耦 ─────────────────────────────────────────────────────
/// 物理模拟运行在独立线程（60 FPS 固定步长）。为让渲染线程无锁读取，
/// 本类维护"双缓冲快照"：每次 Step 结束把最新坐标写入新数组，然后
/// 整体替换 _snapX/_snapY 引用（引用赋值是原子的）。渲染线程只需要
/// 持有当前快照引用，即可得到一帧内完全一致的网格数据，无需加锁。
/// </summary>
public sealed class SpringMassGrid
{
    // ── 网格结构 ──────────────────────────────────────────────────────
    /// <summary>网格列数（横向质点数）</summary>
    public int Cols { get; private set; }

    /// <summary>网格行数（纵向质点数）</summary>
    public int Rows { get; private set; }

    /// <summary>网格单元尺寸（像素），与 PhysicsParams.GridCellSize 同步</summary>
    public double CellSize { get; private set; }

    /// <summary>网格覆盖的宽度（像素）</summary>
    public double Width { get; }

    /// <summary>网格覆盖的高度（像素）</summary>
    public double Height { get; }

    /// <summary>所有质点，一维存储，下标 = row * Cols + col</summary>
    private MassPoint[] _points = Array.Empty<MassPoint>();

    /// <summary>共享的可调参数对象（UI 修改立即生效）</summary>
    private readonly PhysicsParams _p;

    // ── 鼠标拖拽状态（由 SimulationLoop 每帧更新）─────────────────────
    private bool _dragging;
    private double _mx, _my;

    // ── 渲染双缓冲快照（volatile 引用，跨线程可见性由 volatile 保证）──
    private volatile float[] _snapX = Array.Empty<float>();
    private volatile float[] _snapY = Array.Empty<float>();

    /// <summary>渲染线程读取的最新坐标快照（X），长度 = Cols*Rows</summary>
    public float[] SnapX => _snapX;

    /// <summary>渲染线程读取的最新坐标快照（Y），长度 = Cols*Rows</summary>
    public float[] SnapY => _snapY;

    /// <summary>
    /// 最近一次步进后的最大单点位移（像素）。渲染层用它决定当前帧
    /// 是否需要叠加"形变网格层"（位移极小 → 视觉上等同原图，直接画基础层即可）。
    /// </summary>
    public float LastMaxDisplacement { get; private set; }

    /// <summary>速度上限（像素/秒），防止数值爆炸导致质点飞走</summary>
    private const double MaxVelocity = 4000.0;

    // 每帧受力累加缓冲（物理线程独占，避免每帧分配垃圾）
    private double[] _fx = Array.Empty<double>();
    private double[] _fy = Array.Empty<double>();

    public SpringMassGrid(double width, double height, PhysicsParams physics)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "网格尺寸必须为正");

        Width = width;
        Height = height;
        _p = physics;
        Rebuild();
    }

    /// <summary>
    /// （重新）构建网格：根据当前 GridCellSize 在 [0,Width]×[0,Height]
    /// 上均匀铺点。网格尺寸参数变更时由调用方调用本方法。
    /// </summary>
    public void Rebuild()
    {
        CellSize = Math.Max(8.0, _p.GridCellSize);

        // 列数 = ceil(Width/CellSize)+1，保证覆盖整个屏幕（含右/下边界）
        Cols = (int)Math.Ceiling(Width / CellSize) + 1;
        Rows = (int)Math.Ceiling(Height / CellSize) + 1;

        var pts = new MassPoint[Cols * Rows];
        for (int r = 0; r < Rows; r++)
        {
            double y = Math.Min(r * CellSize, Height);
            for (int c = 0; c < Cols; c++)
            {
                double x = Math.Min(c * CellSize, Width);
                pts[r * Cols + c] = new MassPoint(x, y);
            }
        }
        _points = pts;
        _fx = new double[pts.Length];
        _fy = new double[pts.Length];
        PublishSnapshot();
    }

    /// <summary>质点下标辅助：row * Cols + col</summary>
    public int IndexOf(int col, int row) => row * Cols + col;

    /// <summary>按下标访问质点（物理线程专用）</summary>
    public MassPoint GetPoint(int index) => _points[index];

    /// <summary>
    /// 设置鼠标拖拽状态。SimulationLoop 在每次物理步进前调用：
    /// 鼠标左键按下且不在图标上 → dragging=true，并给出鼠标坐标。
    /// </summary>
    public void SetDrag(double mouseX, double mouseY, bool dragging)
    {
        _dragging = dragging;
        _mx = mouseX;
        _my = mouseY;
    }

    /// <summary>立即松手（等价于 SetDrag(0,0,false)）</summary>
    public void Release() => _dragging = false;

    /// <summary>复位全部质点到原始位置并清零速度</summary>
    public void Reset()
    {
        for (int i = 0; i < _points.Length; i++)
            _points[i].Reset();
        PublishSnapshot();
    }

    /// <summary>当前全部质点的位移能量（Σ|P−P_rest|²），供测试/调参使用</summary>
    public double TotalDisplacementEnergy()
    {
        double e = 0;
        for (int i = 0; i < _points.Length; i++)
        {
            var p = _points[i];
            double dx = p.X - p.RestX, dy = p.Y - p.RestY;
            e += dx * dx + dy * dy;
        }
        return e;
    }

    /// <summary>当前最大单点位移（像素），供测试/调参使用</summary>
    public double MaxDisplacementNow()
    {
        double m = 0;
        for (int i = 0; i < _points.Length; i++)
        {
            double d = _points[i].DisplacementLength();
            if (d > m) m = d;
        }
        return m;
    }

    /// <summary>
    /// 固定步长物理步进：dt 秒（推荐 1/60）。
    ///
    /// ── 力学公式（每个质点独立计算，m = 1）────────────────────────────
    ///
    /// ① 弹簧力（胡克定律 Hooke's law，牛顿第三定律：两端等大反向）
    ///    两个相邻质点 P、Q，当前距离 L = |P−Q|，静止长度 L₀ = |P_rest−Q_rest|。
    ///    弹簧对 P 的力沿 PQ 连线方向、大小与形变量成正比：
    ///        F_s(P) = k · (L₀ − L) · û ，û = (P−Q)/L 为单位方向向量
    ///    对 Q 施加 −F_s(P)（等大反向）。L > L₀（被拉长）→ 收缩；
    ///    L < L₀（被压缩）→ 拉伸。实现上先累加所有力到 _fx/_fy 数组，
    ///    保证每个弹簧的两端都收到正确的反作用力（动量守恒，能量只降不升）。
    ///
    /// ② 锚点力（弱弹簧拉回原始坐标，防止网格整体漂移）
    ///        F_a = k_a · (P_rest − P) ，k_a = 0.15·k
    ///    相当于每个质点都有一根指向"出生位置"的软弹簧。
    ///
    /// ③ 粘性阻尼（viscous damping，耗散动能让震荡收敛）
    ///        F_d = −c · v ，c = ζ · 2·√(k·m)，ζ = Damping（阻尼比）
    ///    2·√(k·m) 是该弹簧系统的"临界阻尼系数"：
    ///      ζ=1 时系统恰好不震荡地最快回位；0&lt;ζ&lt;1 时欠阻尼产生 Q 弹往复。
    ///
    /// ④ 拖拽外力（鼠标，仅影响半径 R 内的质点，带平方衰减）
    ///        F_p = k_drag · (P_mouse − P) · (1 − d/R)² ，k_drag = 2·DragStrength·k
    ///    其中 d = |P_mouse − P|。距离鼠标越近，跟随越紧；
    ///    在半径边缘 (d→R) 处力连续衰减到 0，避免网格被"撕开"。
    ///
    /// ⑤ 半隐式欧拉积分
    ///        v ← v + F·dt            （m=1，F 即加速度）
    ///        x ← x + v·dt            （使用更新后的速度）
    ///
    /// ⑥ 两个安全钳制：
    ///        |v| ≤ MaxVelocity                      —— 防止数值爆炸
    ///        |P − P_rest| ≤ MaxDisplacement         —— 防止网格极端扭曲
    /// </summary>
    public void Step(double dt)
    {
        if (dt <= 0) return;

        double k      = _p.Stiffness;
        double anchorK = k * 0.15;
        double c      = _p.Damping * 2.0 * Math.Sqrt(k);   // 阻尼系数 c = ζ·2√(k·m)
        double kDrag  = 2.0 * _p.DragStrength * k;          // 拖拽外力比例系数
        double radius = _p.DragRadius;
        double maxDisp = _p.MaxDisplacement;
        int cols = Cols;

        MassPoint[] pts = _points;
        int count = pts.Length;
        double[] fx = _fx, fy = _fy;

        // 第一遍：清空受力缓冲
        Array.Clear(fx, 0, count);
        Array.Clear(fy, 0, count);

        for (int idx = 0; idx < count; idx++)
        {
            MassPoint p = pts[idx];
            int col = idx % cols;
            int row = idx / cols;

            // ── 右邻居弹簧：对 p 施加 F，对邻居施加 −F ──
            if (col + 1 < Cols)
            {
                int qi = idx + 1;
                ApplySpringForce(p, pts[qi], k,
                    ref fx[idx], ref fy[idx], ref fx[qi], ref fy[qi]);
            }

            // ── 下邻居弹簧：同上 ──
            if (row + 1 < Rows)
            {
                int qi = idx + cols;
                ApplySpringForce(p, pts[qi], k,
                    ref fx[idx], ref fy[idx], ref fx[qi], ref fy[qi]);
            }

            // ── 锚点力：弱弹簧拉回原始坐标 ──
            fx[idx] += anchorK * (p.RestX - p.X);
            fy[idx] += anchorK * (p.RestY - p.Y);

            // ── 粘性阻尼：F = −c·v ──
            fx[idx] -= c * p.Vx;
            fy[idx] -= c * p.Vy;

            // ── 拖拽外力（仅按下时生效）──
            if (_dragging)
            {
                double dx = _mx - p.X;
                double dy = _my - p.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);

                if (d < radius && d > 0.5)
                {
                    // 平方衰减系数：(1 − d/R)² ∈ [0,1]
                    double falloff = 1.0 - d / radius;
                    falloff *= falloff;

                    // F_p = k_drag · (P_mouse − P) · falloff
                    fx[idx] += kDrag * falloff * dx;
                    fy[idx] += kDrag * falloff * dy;

                    // 靠近鼠标中心时额外抑制速度，避免质点绕着鼠标抖动
                    if (d < radius * 0.5)
                    {
                        p.Vx *= 0.85;
                        p.Vy *= 0.85;
                    }
                }
            }
        }

        // 第二遍：半隐式欧拉积分 + 安全钳制
        for (int idx = 0; idx < count; idx++)
        {
            MassPoint p = pts[idx];

            // 半隐式欧拉：先更新速度，再用新速度更新位置
            p.Vx += fx[idx] * dt;
            p.Vy += fy[idx] * dt;

            // 速度钳制（防止数值爆炸）
            double sp = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            if (sp > MaxVelocity)
            {
                double s = MaxVelocity / sp;
                p.Vx *= s;
                p.Vy *= s;
            }

            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;

            // 最大位移钳制：把超出的位移向量等比缩放回允许半径内
            double ox = p.X - p.RestX;
            double oy = p.Y - p.RestY;
            double od = Math.Sqrt(ox * ox + oy * oy);
            if (od > maxDisp)
            {
                double s = maxDisp / od;
                p.X = p.RestX + ox * s;
                p.Y = p.RestY + oy * s;
            }
        }

        // 物理步进结束 → 发布最新快照（整体替换引用，渲染线程无锁读取）
        PublishSnapshot();
    }

    /// <summary>
    /// 计算两质点间弹簧力，并把"等大反向"的力分别累加到两个质点上
    /// （满足牛顿第三定律，保证系统动量守恒、能量只降不升）。
    /// 胡克定律：F = k·(L₀ − L)·û。
    /// L₀ 用两质点的"静止距离"（由原始坐标算出），这样边界处不规则的单元
    /// （右/下边界剩余宽度）也能得到正确的静止长度。
    /// </summary>
    private static void ApplySpringForce(MassPoint p, MassPoint q, double k,
                                         ref double fxP, ref double fyP,
                                         ref double fxQ, ref double fyQ)
    {
        // 当前向量与当前距离 L（方向 û = (P−Q)/L）
        double dx = p.X - q.X;
        double dy = p.Y - q.Y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        if (l < 1e-6) return; // 两点重合时方向未定义，跳过，避免除零

        // 静止长度 L₀：由两质点的原始坐标决定（弹簧的"自然长度"）
        double rx = p.RestX - q.RestX;
        double ry = p.RestY - q.RestY;
        double l0 = Math.Sqrt(rx * rx + ry * ry);
        if (l0 < 1e-6) return;

        // 标量力系数：F = k·(L₀ − L)/L 再乘位移向量得到力矢量
        double f = k * (l0 - l) / l;

        // 对 P 施加 F，对 Q 施加 −F（牛顿第三定律）
        fxP += f * dx;
        fyP += f * dy;
        fxQ -= f * dx;
        fyQ -= f * dy;
    }

    /// <summary>把当前坐标写入新数组并整体替换快照引用（双缓冲发布）</summary>
    private void PublishSnapshot()
    {
        int count = _points.Length;
        var nx = new float[count];
        var ny = new float[count];
        float maxDisp = 0f;
        for (int i = 0; i < count; i++)
        {
            var p = _points[i];
            nx[i] = (float)p.X;
            ny[i] = (float)p.Y;
            // 顺带统计最大位移（渲染层据此决定是否叠加形变层）
            float dx = (float)(p.X - p.RestX);
            float dy = (float)(p.Y - p.RestY);
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d > maxDisp) maxDisp = d;
        }
        LastMaxDisplacement = maxDisp;
        _snapX = nx;
        _snapY = ny;
    }
}
