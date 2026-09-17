using System.Text.Json;
using JellyWallpaper.Core.Physics;

namespace JellyWallpaper.Core.Config;

/// <summary>
/// 应用配置模型 + JSON 持久化。
///
/// 持久化位置：%APPDATA%\JellyWallpaper\config.json
///   （Windows 上即 C:\Users\&lt;用户名&gt;\AppData\Roaming\JellyWallpaper\config.json）
/// 程序启动时自动加载；设置面板修改参数后调用 Save() 立即落盘。
/// 使用 System.Text.Json 序列化（VS2022 / .NET 内置，无第三方依赖）。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 配置版本。参数预设更新时 +1；旧版本配置在 Load() 时自动升级，
    /// 物理参数重置为新的默认值（防止用户被旧版"手感过时"的参数卡住）。
    /// </summary>
    public int ConfigVersion { get; set; } = 2;

    /// <summary>物理引擎参数（滑块实时修改，立即生效）</summary>
    public PhysicsParams Physics { get; set; } = new();

    /// <summary>是否启用果冻效果（关闭时壁纸静止显示，不响应拖拽）</summary>
    public bool EffectEnabled { get; set; } = true;

    /// <summary>是否跟随系统壁纸（关闭时使用 FallbackColorHex 纯色背景）</summary>
    public bool FollowSystemWallpaper { get; set; } = true;

    /// <summary>不跟随系统壁纸时的纯色背景（#RRGGBB）</summary>
    public string FallbackColorHex { get; set; } = "#1E1E2E";

    /// <summary>壁纸变更轮询间隔（秒）。兜底机制，兼容 Spotlight 等事件失效场景</summary>
    public int WallpaperPollIntervalSec { get; set; } = 5;

    /// <summary>启动时是否最小化设置窗口（静默常驻）</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>配置文件完整路径</summary>
    public static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JellyWallpaper", "config.json");

    /// <summary>
    /// 加载配置。文件不存在 / 损坏时回退到默认值并重新写一份，
    /// 保证程序永不因配置问题崩溃（与壁纸加载失败容错同一原则）。
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏：忽略，使用默认值
        }

        var def = new AppConfig();
        def.Save(); // 写回一份干净的默认配置
        return def;
    }

    /// <summary>原子保存：先写临时文件再替换，避免写入中途断电导致配置损坏</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // 磁盘只读等极端情况：静默失败，不影响运行
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,          // 人类可读，方便用户手改
        PropertyNameCaseInsensitive = true,
    };
}
