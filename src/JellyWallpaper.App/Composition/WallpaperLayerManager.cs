using System.Drawing;
using System.Windows.Forms;
using JellyWallpaper.App.Desktop;
using JellyWallpaper.App.Lifecycle;
using JellyWallpaper.App.Native;
using JellyWallpaper.App.Simulation;
using JellyWallpaper.App.Wallpaper;
using JellyWallpaper.Core.Config;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Windows.System;
// 屏幕像素矩形（Screen.Bounds 返回类型）：显式别名消除歧义
// （System.Drawing.Rectangle vs System.Windows.Shapes.Rectangle）
using Rectangle = System.Drawing.Rectangle;
using Device = SharpDX.Direct3D11.Device;


namespace JellyWallpaper.App.Composition;

/// <summary>
/// 壁纸层总管理器 —— 所有模块的装配与生命周期中枢。
///
/// 线程模型（三层，各自解耦）：
///   ① 主线程（WPF）：设置 UI，读写 AppConfig；
///   ② 合成线程（专用 DispatcherQueue 线程）：Compositor / Direct2D / 渲染，
///      所有 Composition 对象与渲染循环都在这里；
///   ③ 物理线程（SimulationLoop）：60FPS 固定步长模拟，只写网格快照。
///
/// GPU 设备链（v4.5）：
///   D3D11 Device（SharpDX）→ QI IDXGIDevice
///     → D2D Factory1.CreateDevice(IDXGIDevice) → ID2D1Device
///     → ICompositorInterop::CreateGraphicsDevice(ID2D1Device)
///       → CompositionGraphicsDevice（创建绘制表面的来源）
///
/// 职责：
///   * 启动合成线程，初始化 DWM 桌面合成宿主（挂到 Progman）；
///   * 为每块屏幕创建独立 MonitorRenderLayer（视觉/表面/网格实例）；
///   * 接线：壁纸变化 → 异步加载纹理；explorer 重启 → 销毁并重建合成层；
///   * 向设置窗口暴露 Config 以便实时调参。
/// </summary>
public sealed class WallpaperLayerManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly DispatcherQueueController _queueController;
    private readonly DispatcherQueue _queue;
    private readonly DesktopWallpaperHost _host = new();
    private readonly Device _d3d;
    private readonly SharpDX.Direct2D1.Device _d2dDevice; // 长期持有（Composition 图形设备底层）
    private readonly IntPtr _d2dDevicePtr;
    private readonly SimulationLoop _simulation;
    private readonly WallpaperWatcher _wallpaperWatcher;
    private readonly ExplorerWatcher _explorerWatcher;
    private string? _lastLoggedError; // 去重：错误文本不变时只写一次日志

    /// <summary>v5.30：原生壁纸隐藏失败时周期重试（explorer 重启 / WorkerW 未就绪场景）</summary>
    private DispatcherQueueTimer? _hideRetryTimer;

    /// <summary>当前所有屏幕的渲染层（物理线程每帧读取；重建时整体替换引用）</summary>
    private volatile IReadOnlyList<MonitorRenderLayer> _layers = Array.Empty<MonitorRenderLayer>();
    private volatile bool _disposed;

    /// <summary>渲染层集合（供物理线程遍历步进）</summary>
    public IReadOnlyList<MonitorRenderLayer> Layers => _layers;

    /// <summary>配置对象（设置窗口直接修改，物理/渲染线程每帧读取最新值）</summary>
    public AppConfig Config => _config;

    /// <summary>桌面合成是否已就绪（供 UI 显示状态）</summary>
    public bool HostReady => _host.IsInitialized;

    /// <summary>最近一次挂载失败的详细原因（UI 状态栏显示，用于快速定位）</summary>
    public string? LastInitError => _host.LastError;

    /// <summary>
    /// 运行时诊断汇总（UI 状态栏每秒显示）：
    /// 渲染 FPS / 纹理 / 鼠标拖拽 / 网格位移 / 壁纸窗口可见性。
    /// 用于定位"能打开但拖拽没效果"的具体环节。
    /// </summary>
    public string Diagnostics
    {
        get
        {
            var layers = _layers;
            if (layers.Count == 0) return "无渲染层（尚未挂载完成）";
            var l = layers[0];
            string renderErr = l.LastRenderError ?? "";
            string initErr = l.InitError ?? "";
            string path = l.RenderPath;
            string initPart = initErr.Length > 0 ? " 初始化异常:" + initErr : "";
            string hidePart = _host.NativeWallpaperHidden ? " 原生壁纸=已隐藏" : " 原生壁纸=未隐藏";
            string hideInfo = " " + _host.LastHideInfo;
            return $"FPS={l.RenderFps:F0} 纹理={l.TextureInfo} 壁纸读取={WallpaperSource.LastReadInfo} {_simulation.LastDragInfo} {_host.HostWindowInfo}{hidePart}{hideInfo}{(path.Length > 0 ? $" 路径={path}" : "")}{(renderErr.Length > 0 ? " 渲染异常:" + renderErr : "")}{initPart} v5.34";
        }
    }

    public WallpaperLayerManager(AppConfig config)
    {
        _config = config;

        // ① 专用合成线程：所有 Composition/D2D 对象必须在这里创建/使用
        _queueController = DispatcherQueueController.CreateOnDedicatedThread();
        _queue = _queueController.DispatcherQueue;

        // 共享 GPU 设备链：D3D11 → IDXGIDevice → ID2D1Device
        // （硬件加速；无 GPU 环境回退 WARP 软件渲染，虚拟机/远程桌面也能跑。
        //  SharpDX 的 D2D Device 直接由 DXGI 设备构造，无需 Factory.CreateDevice）
        try
        {
            _d3d = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch
        {
            _d3d = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        }

        _d2dDevice = new SharpDX.Direct2D1.Device(_d3d.QueryInterface<SharpDX.DXGI.Device>());
        _d2dDevicePtr = _d2dDevice.NativePointer;

        _simulation = new SimulationLoop(this, config);
        _wallpaperWatcher = new WallpaperWatcher(config, Screen.AllScreens.Length);
        _explorerWatcher = new ExplorerWatcher();
    }

    /// <summary>启动全部子系统（在主线程调用）</summary>
    public void Start()
    {
        _queue.TryEnqueue(InitializeOnCompositorThread);

        _simulation.Start();

        _wallpaperWatcher.WallpaperChanged += OnWallpaperChanged;
        _wallpaperWatcher.Start();

        _explorerWatcher.ExplorerRestarted += OnExplorerRestarted;
        _explorerWatcher.Start();

        // v5.30：隐藏原生壁纸的重试保障 —— 程序启动/explorer 重启初期
        // WorkerW 可能尚未就绪，首次隐藏失败会导致原生壁纸盖住本程序渲染层
        // （表现为"拖拽没效果、看到的是系统壁纸"）。每 2 秒复查一次，
        // 一旦发现原生壁纸未被隐藏就补隐藏，直到成功。
        StartHideRetryTimer();
    }

    /// <summary>启动"原生壁纸隐藏"周期复查（合成线程定时器，2 秒一次）</summary>
    private void StartHideRetryTimer()
    {
        if (_hideRetryTimer != null || _disposed) return;
        var t = _queue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.IsRepeating = true;
        t.Tick += (s, e) =>
        {
            if (_disposed)
            {
                t.Stop();
                return;
            }
            if (!_host.NativeWallpaperHidden)
                _host.HideNativeWallpaper();
        };
        t.Start();
        _hideRetryTimer = t;
    }

    /// <summary>设置窗口修改了壁纸相关开关（跟随系统壁纸）后，立即重新应用</summary>
    public void RefreshWallpaper()
    {
        _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            var infos = WallpaperSource.ReadAllMonitors(_layers.Count);
            ApplyWallpaperInfos(infos);
        });
    }

    // ── 合成线程：初始化 + 建层 ────────────────────────────────────────
    private void InitializeOnCompositorThread()
    {
        if (_disposed) return;

        // 宿主挂载：用 Direct2D 设备指针（ID2D1Device）创建组合图形设备
        if (_host.Initialize(_d2dDevicePtr))
        {
            BuildLayers();
        }
        else
        {
            // 挂载失败：把详细原因写日志（错误文本不变时不重复写），
            // 并每 1 秒重试（开机启动/explorer 重启初期 explorer 未就绪）
            var err = _host.LastError;
            if (!string.IsNullOrEmpty(err) && err != _lastLoggedError)
            {
                _lastLoggedError = err;
                App.LogError("CompositorMount", new Exception(err));
            }

            var retry = _queue.CreateTimer();
            retry.Interval = TimeSpan.FromSeconds(1);
            retry.IsRepeating = false;
            retry.Tick += (s, e) =>
            {
                retry.Stop();
                InitializeOnCompositorThread();
            };
            retry.Start();
        }
    }

    /// <summary>销毁旧层并按当前屏幕布局重建（explorer 重启 / 初次建层时调用）</summary>
    private void BuildLayers()
    {
        foreach (var old in _layers)
            old.Dispose();

        // v5.19：隐藏原生壁纸 WorkerW —— 让本程序渲染层成为可见壁纸画面
        _host.HideNativeWallpaper();

        var list = new List<MonitorRenderLayer>(Screen.AllScreens.Length);
        foreach (var screen in Screen.AllScreens)
        {
            Rectangle bounds = screen.Bounds;
            float dpi = GetMonitorDpi(bounds);

            var layer = new MonitorRenderLayer(_host, _config, _queue, bounds, dpi, _d3d.NativePointer);
            layer.CreateVisual();
            list.Add(layer);
        }
        _layers = list;

        // 建层后立即应用当前壁纸
        var infos = WallpaperSource.ReadAllMonitors(list.Count);
        ApplyWallpaperInfos(infos);
    }

    /// <summary>把壁纸信息逐个推给对应屏幕层（异步加载纹理，不阻塞渲染）</summary>
    private void ApplyWallpaperInfos(IReadOnlyList<WallpaperImageInfo> infos)
    {
        // 壁纸切换后 WorkerW 可能重建 → 重新隐藏原生壁纸层
        _host.HideNativeWallpaper();

        var layers = _layers;
        for (int i = 0; i < Math.Min(layers.Count, infos.Count); i++)
        {
            int idx = i;
            var info = infos[i];
            _queue.TryEnqueue(() =>
            {
                if (_disposed || idx >= _layers.Count) return;
                _layers[idx].UpdateWallpaper(info);
            });
        }
    }

    // ── 事件接线 ──────────────────────────────────────────────────────
    private void OnWallpaperChanged(IReadOnlyList<WallpaperImageInfo> infos)
        => ApplyWallpaperInfos(infos);

    /// <summary>explorer 重启：销毁旧 DWM 合成层，重建图层与渲染资源</summary>
    private void OnExplorerRestarted()
    {
        _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _host.Teardown();          // 旧合成目标已随 explorer 消亡
            InitializeOnCompositorThread(); // 重新挂载 + 重建所有屏幕层
        });
    }

    /// <summary>获取指定屏幕的 DPI（物理像素口径，PerMonitorV2）</summary>
    private static float GetMonitorDpi(Rectangle bounds)
    {
        try
        {
            var pt = new NativeMethods.POINT
            {
                X = bounds.X + bounds.Width / 2,
                Y = bounds.Y + bounds.Height / 2
            };
            IntPtr hmon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(hmon, NativeMethods.MonitorDpiType.Effective,
                                               out uint dpiX, out _) == 0)
            {
                return dpiX;
            }
        }
        catch { /* 回退 96 */ }
        return 96f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hideRetryTimer?.Stop();
        _explorerWatcher.Dispose();
        _wallpaperWatcher.Dispose();
        _simulation.Dispose();

        // 恢复原生壁纸（把隐藏的 WorkerW 显示回来，程序退出不留后遗症）
        _host.RestoreNativeWallpaper();

        // 合成线程上释放所有 GPU 资源并关闭队列线程
        _queue.TryEnqueue(() =>
        {
            foreach (var layer in _layers)
                layer.Dispose();
            _layers = Array.Empty<MonitorRenderLayer>();
            _host.Teardown();
            _d2dDevice.Dispose();
            
            _ = _queueController.ShutdownQueueAsync();
        });

        // D3D 设备仅在设备链创建时使用，可立即释放
        _d3d.Dispose();
    }
}
