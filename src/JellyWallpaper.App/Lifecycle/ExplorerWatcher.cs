using System.Diagnostics;

namespace JellyWallpaper.App.Lifecycle;

/// <summary>
/// Explorer 重启自动恢复（模块⑥）。
///
/// explorer.exe 崩溃/重启会销毁桌面的 Progman 窗口，导致我们挂载在
/// 桌面上的合成层随之消失。本模块轮询 explorer 进程状态，检测到
/// "运行中 → 退出 → 重新出现" 的完整重启周期后触发 ExplorerRestarted
/// 事件，由 WallpaperLayerManager 在合成线程销毁旧 DWM 资源并重建
/// 合成图层与渲染资源，无需用户重启软件。
///
/// 轮询间隔 2 秒：重启检测延迟可接受（壁纸层重建 < 100ms）。
/// </summary>
public sealed class ExplorerWatcher : IDisposable
{
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>explorer 完成一次"退出→重新启动"周期后触发</summary>
    public event Action? ExplorerRestarted;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ExplorerWatch" };
        _thread.Start();
    }

    private void Loop()
    {
        bool wasRunning = IsExplorerRunning();
        bool sawDown = false;

        while (!_stop)
        {
            Thread.Sleep(2000);

            bool running = IsExplorerRunning();

            if (wasRunning && !running)
            {
                sawDown = true;            // 观察到 explorer 退出
            }
            else if (!wasRunning && running)
            {
                if (sawDown)
                    ExplorerRestarted?.Invoke(); // 完整的 退出→重启 周期
                sawDown = false;
            }

            wasRunning = running;
        }
    }

    private static bool IsExplorerRunning()
    {
        try
        {
            return Process.GetProcessesByName("explorer").Length > 0;
        }
        catch
        {
            return false; // 权限等异常按"未运行"处理
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
    }
}
