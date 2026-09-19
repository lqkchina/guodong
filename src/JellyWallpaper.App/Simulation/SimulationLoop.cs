using System.Diagnostics;
using JellyWallpaper.App.Composition;
using JellyWallpaper.App.Desktop;
using JellyWallpaper.App.Input;
using JellyWallpaper.Core.Config;
using JellyWallpaper.Core.Physics;

namespace JellyWallpaper.App.Simulation;

/// <summary>
/// 物理模拟线程（模块②的"独立线程 + 固定步长 60FPS"部分）。
///
/// ── 线程模型 ─────────────────────────────────────────────────────
/// 本线程独立于渲染线程（合成线程）运行：
///   * 固定步长 dt = 1/60s（累积器模式，一次循环补齐所有欠账，防掉帧螺旋）；
///   * 每帧：采样鼠标 → 图标命中检测 → 决定拖拽状态 → 步进所有屏幕网格；
///   * 渲染线程只读网格的双缓冲快照（volatile 引用交换），无锁解耦。
///
/// ── 鼠标逻辑（对应需求）──────────────────────────────────────────
///   左键按下 + 鼠标在【桌面空白区域】（未命中任何图标）→ 仅该屏幕网格
///   内的质点受拖拽力；命中图标 → 不触发形变（图标走原生行为）。
///   松开 → 弹簧力 + 阻尼驱动回弹（由 SpringMassGrid.Step 完成）。
/// </summary>
public sealed class SimulationLoop : IDisposable
{
    private const double StepDt = 1.0 / 60.0; // 固定步长 60FPS

    private readonly WallpaperLayerManager _manager;
    private readonly AppConfig _config;
    private readonly MouseInputService _mouse = new();
    private readonly DesktopIconTracker _icons = new();

    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>诊断信息：鼠标坐标 / 拖拽状态 / 命中图标 / 最大位移（UI 每秒显示）</summary>
    public volatile string LastDragInfo = "物理线程未启动";

    public SimulationLoop(WallpaperLayerManager manager, AppConfig config)
    {
        _manager = manager;
        _config = config;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PhysicsLoop" };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        double accumulator = 0;
        long prevMs = sw.ElapsedMilliseconds;

        while (!_stop)
        {
            long nowMs = sw.ElapsedMilliseconds;
            accumulator += (nowMs - prevMs) / 1000.0;
            prevMs = nowMs;

            // 固定步长：一次循环内最多补 8 步（≈133ms），防止长时间卡顿螺旋
            int steps = 0;
            while (accumulator >= StepDt && steps < 8)
            {
                TickPhysics();
                accumulator -= StepDt;
                steps++;
            }
            if (steps >= 8) accumulator = 0; // 掉帧保护：丢弃积压

            // 保持 ~60Hz 节奏
            Thread.Sleep(1);
        }
    }

    /// <summary>单步物理：采样输入 → 命中检测 → 步进全部屏幕网格</summary>
    private void TickPhysics()
    {
        var layers = _manager.Layers;
        if (layers.Count == 0) return;

        var mouse = _mouse.Sample();

        // 图标矩形每 500ms 刷新一次；命中图标则本帧不触发任何形变
        _icons.RefreshIfStale();
        bool overIcon = _icons.IsOverIcon(mouse.X, mouse.Y);
        bool dragAllowed = _config.EffectEnabled && !overIcon;

        foreach (var layer in layers)
        {
            // 首次为该屏幕创建物理网格（网格是纯数学对象，物理线程可直接创建）
            var grid = layer.Grid;
            if (grid == null)
            {
                grid = new SpringMassGrid(layer.Bounds.Width, layer.Bounds.Height, _config.Physics);
                layer.Grid = grid;
            }

            // 网格密度参数变化 → 重建网格（设置面板实时修改立即生效）
            if (Math.Abs(grid.CellSize - _config.Physics.GridCellSize) > 0.01)
                grid.Rebuild();

            // 仅当鼠标落在这块屏幕范围内才允许拖拽（多显示器互不干扰）
            // v5.22 修复：必须"左键按下"才进入拖拽（此前只判断鼠标位置，
            // 导致鼠标悬停就持续向鼠标位置收缩）
            bool inThisMonitor = layer.Bounds.Contains(mouse.X, mouse.Y);
            bool dragging = dragAllowed && inThisMonitor && mouse.LeftDown;

            // 拖拽坐标转为网格局部坐标（0..w, 0..h）
            grid.SetDrag(mouse.X - layer.Bounds.X, mouse.Y - layer.Bounds.Y, dragging);
            grid.Step(StepDt);
        }

        // 诊断快照（UI 状态栏每秒显示；LastMaxDisplacement 由渲染层读取）
        float maxDisp = 0f;
        foreach (var l in _manager.Layers)
            if (l.Grid != null) maxDisp = Math.Max(maxDisp, l.Grid.LastMaxDisplacement);
        LastDragInfo = $"鼠标=({mouse.X},{mouse.Y}) 拖拽={(dragAllowed ? "是" : "否")} 命中图标={(overIcon ? "是" : "否")} 网格={layers.Count}块 位移={maxDisp:F1}px 效果={(dragAllowed ? "开" : "关")}";
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
    }
}
