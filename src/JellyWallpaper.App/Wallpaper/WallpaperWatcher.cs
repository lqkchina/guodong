using JellyWallpaper.Core.Config;

namespace JellyWallpaper.App.Wallpaper;

/// <summary>
/// 壁纸变更监听模块（模块①的"监听"部分）。
///
/// 双通道检测：
///   1. SystemEvents.UserPreferenceChanged —— 系统在壁纸切换时常触发（事件通道）；
///   2. 兜底轮询 —— 每 WallpaperPollIntervalSec 秒重新读取注册表并比较
///      "路径|修改时间|内嵌BMP长度" 签名。Windows Spotlight 聚焦壁纸
///      在某些版本上事件通知失效，轮询保证一定能发现变化（兜底机制）。
///
/// 检测到变化后通过 WallpaperChanged 事件抛出，由 WallpaperLayerManager
/// 在合成线程异步加载纹理 —— 加载不阻塞物理/渲染循环（模块①要求）。
/// </summary>
public sealed class WallpaperWatcher : IDisposable
{
    private readonly AppConfig _config;
    private readonly int _monitorCount;
    private Thread? _pollThread;
    private volatile bool _stop;
    private string[] _signatures = Array.Empty<string>();
    private readonly object _gate = new();

    /// <summary>壁纸变化事件：参数为每块屏幕的最新壁纸信息（按屏幕顺序）</summary>
    public event Action<IReadOnlyList<WallpaperImageInfo>>? WallpaperChanged;

    public WallpaperWatcher(AppConfig config, int monitorCount)
    {
        _config = config;
        _monitorCount = monitorCount;
    }

    /// <summary>启动监听（须在 UI/主线程调用，SystemEvents 需要消息泵）</summary>
    public void Start()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "WallpaperPoll" };
        _pollThread.Start();
    }

    /// <summary>立即执行一次检测并（如有变化）触发事件。层构建完成后调用。</summary>
    public void TriggerImmediate() => CheckAndRaise();

    private void PollLoop()
    {
        int intervalMs = Math.Max(1, _config.WallpaperPollIntervalSec) * 1000;
        while (!_stop)
        {
            Thread.Sleep(intervalMs);
            CheckAndRaise();
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // 壁纸/颜色类设置变化时立刻检查一次（事件通道）
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
            e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop)
        {
            CheckAndRaise();
        }
    }

    /// <summary>
    /// 读取全部屏幕壁纸并比较签名；有变化才触发事件，避免高频无意义刷新。
    /// </summary>
    private void CheckAndRaise()
    {
        IReadOnlyList<WallpaperImageInfo> infos;
        try
        {
            infos = WallpaperSource.ReadAllMonitors(_monitorCount);
        }
        catch
        {
            return; // 读取失败：静默跳过，等下一轮
        }

        var sig = new string[infos.Count];
        for (int i = 0; i < infos.Count; i++)
            sig[i] = $"{infos[i].Path}|{infos[i].LastWriteUtc.Ticks}|{infos[i].EmbeddedBmp?.Length ?? 0}";

        lock (_gate)
        {
            if (sig.SequenceEqual(_signatures)) return;
            _signatures = sig;
        }

        WallpaperChanged?.Invoke(infos);
    }

    public void Dispose()
    {
        _stop = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _pollThread?.Join(500);
    }
}
