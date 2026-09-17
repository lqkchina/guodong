namespace JellyWallpaper.Core.Physics;

/// <summary>
/// 弹簧-质点网格的全部可调物理参数。
/// 设置 UI 面板修改任意参数后立即生效（Step() 每帧读取本对象的当前值，
/// 无需重启网格）。所有单位均为像素/秒制。
/// </summary>
public sealed class PhysicsParams
{
    /// <summary>
    /// 网格单元尺寸（像素）。控制网格密度：
    /// 越小网格越密，果冻形变的细节越丰富，但物理计算与 GPU 绘制开销越大。
    /// 建议范围 12 ~ 120，默认 40（Q 弹预设：细节与整体感平衡）。
    /// 修改后需要重建网格（由调用方在 UI 变更时触发 Rebuild）。
    /// </summary>
    public double GridCellSize { get; set; } = 40.0;

    /// <summary>
    /// 弹簧刚度 Stiffness（决定回弹力度）。
    /// 物理意义：胡克定律中的弹性系数 k（单位像素/秒² 量级）。
    /// 越大 → 回弹越快、手感越"硬"；过大会导致数值发散（见 Step 注释中的稳定性条件）。
    /// Q 弹预设 140：松手后快速有力地弹回，不软绵。
    /// </summary>
    public double Stiffness { get; set; } = 140.0;

    /// <summary>
    /// 阻尼 Damping（抑制回弹震荡的阻尼比 ζ，取值 0 ~ 1）。
    /// 物理意义：粘性阻尼系数 c = ζ · 2·√(k·m)，其中 m 为质点质量（归一化为 1）。
    ///   ζ = 0   → 无阻尼，永远震荡；
    ///   0 < ζ < 1 → 欠阻尼，松手后有 Q 弹的往复震荡并逐渐衰减（果冻感来源）；
    ///   ζ = 1   → 临界阻尼，最快回到静止且不震荡；
    ///   ζ > 1   → 过阻尼，缓慢爬回静止，无弹感。
    /// Q 弹预设 0.18：松手后明显"弹回来 + 颤两下再停"；太低(&lt;0.10)会晃个没完。
    /// </summary>
    public double Damping { get; set; } = 0.18;

    /// <summary>
    /// 拖拽影响半径（像素）：鼠标周围多大范围内的质点会被拖拽力影响。
    /// 半径之外的质点完全不受鼠标影响，仅受弹簧网络牵动。
    /// Q 弹预设 260：拉扯范围大 → 形变"整体"。
    /// </summary>
    public double DragRadius { get; set; } = 260.0;

    /// <summary>
    /// 拖拽强度系数（0 ~ 3）。控制拖拽外力的比例系数：
    /// k_drag = 2 · DragStrength · Stiffness。
    /// 越大，质点"跟随鼠标"越紧密。Q 弹预设 1.1。
    /// </summary>
    public double DragStrength { get; set; } = 1.1;

    /// <summary>
    /// 质点最大位移限制（像素）：|P_current − P_rest| 的上限，
    /// 防止网格被拖到极端扭曲甚至撕裂。Q 弹预设 240：形变幅度大。
    /// </summary>
    public double MaxDisplacement { get; set; } = 240.0;
}
