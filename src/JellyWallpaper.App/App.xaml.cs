using System.Windows;
using JellyWallpaper.App.Composition;
using JellyWallpaper.Core.Config;

namespace JellyWallpaper.App;

/// <summary>
/// 应用入口。
/// 职责：单实例互斥 → 加载配置 → 启动壁纸管理器（合成线程 + 物理线程）
/// → 显示 WPF 设置窗口。关闭窗口只隐藏到托盘，程序常驻（见 MainWindow）。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;

    /// <summary>全局壁纸管理器（设置窗口通过它实时修改参数）</summary>
    public static WallpaperLayerManager? Manager { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：重复启动直接退出（壁纸程序不应开两个）
        _mutex = new Mutex(true, @"Global\JellyWallpaper_SingleInstance_7F3A9C", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // 加载配置（损坏时自动回退默认值，见 AppConfig.Load）
        AppConfig config = AppConfig.Load();

        // 启动壁纸管理器：内部创建
        //   ① 专用合成线程（DispatcherQueue，承载 Composition / Win2D 渲染）
        //   ② 物理模拟线程（60FPS 固定步长）
        //   ③ 壁纸监听 / explorer 监听
        Manager = new WallpaperLayerManager(config);
        Manager.Start();

        var window = new MainWindow(config, Manager)
        {
            // 居中显示设置窗口
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        MainWindow = window;
        window.Show();

        // 配置了"启动静默"则直接隐藏到托盘
        if (config.StartMinimized)
            window.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Manager?.Dispose();
        Manager = null;
        base.OnExit(e);
    }
}
