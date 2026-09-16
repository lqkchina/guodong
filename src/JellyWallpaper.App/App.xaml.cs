using System.IO;
using System.Text;
using System.Windows;
using JellyWallpaper.App.Composition;
using JellyWallpaper.Core.Config;

namespace JellyWallpaper.App;

/// <summary>
/// 应用入口。
/// 职责：单实例互斥 → 加载配置 → 启动壁纸管理器（合成线程 + 物理线程）
/// → 显示 WPF 设置窗口。关闭窗口只隐藏到托盘，程序常驻（见 MainWindow）。
///
/// 容错设计（关键）：
///   * OnStartup 整体 try-catch：任何启动异常都写入 %APPDATA%\JellyWallpaper\error.log
///     并弹出错误对话框（而不是静默崩溃退出，方便用户反馈）；
///   * 全局异常钩子（DispatcherUnhandledException / AppDomain.UnhandledException）
///     同样写日志，保证"任何错误都有迹可循"。
///
/// 注意：基类必须用全限定名 System.Windows.Application —— 本工程同时启用了
/// WPF 与 WinForms（托盘图标），两者的隐式 using 都会导入，裸写 Application
/// 会产生 CS0104 歧义编译错误（System.Windows.Forms.Application 与
/// System.Windows.Application 同名）。
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    /// <summary>全局壁纸管理器（设置窗口通过它实时修改参数）</summary>
    public static WallpaperLayerManager? Manager { get; private set; }

    /// <summary>错误日志路径（任何异常都会追加到这里）</summary>
    public static string ErrorLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JellyWallpaper", "error.log");

    /// <summary>追加一行错误日志（带时间戳；写失败静默，绝不因日志再抛异常）</summary>
    public static void LogError(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}");
            File.AppendAllText(ErrorLogPath, sb.ToString());
        }
        catch { /* 日志写失败不影响运行 */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常钩子：任何未处理异常先落日志（不弹崩溃框）
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // 记录后继续运行（界面线程异常不致命）
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        base.OnStartup(e);

        try
        {
            // 单实例：重复启动直接退出（壁纸程序不应开两个）。
            // 用 Local\（会话级）而非 Global\：避免权限/远程会话环境差异导致的
            // UnauthorizedAccessException，同时满足"同一登录会话只有一个实例"。
            _mutex = new Mutex(true, @"Local\JellyWallpaper_SingleInstance_7F3A9C", out bool createdNew);
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
        catch (Exception ex)
        {
            // 启动失败：写日志 + 弹窗（让用户看到具体原因，而不是"双击没反应"）
            LogError("OnStartup", ex);
            System.Windows.MessageBox.Show(
                $"程序启动失败：\n{ex.Message}\n\n详细信息已写入：\n{ErrorLogPath}",
                "果冻弹性壁纸 · 启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Manager?.Dispose();
        Manager = null;
        base.OnExit(e);
    }
}
