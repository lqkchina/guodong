using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JellyWallpaper.App.Wallpaper;

/// <summary>壁纸在屏幕上的填充方式（与系统"背景"设置对应）</summary>
public enum WallpaperFillMode
{
    /// <summary>填充（Cover）：等比缩放裁切铺满，对应系统"填充/拉伸"主流用法</summary>
    Cover = 0,

    /// <summary>拉伸（Stretch）：不平比直接拉满</summary>
    Stretch = 1,

    /// <summary>居中（Center）：原尺寸居中显示</summary>
    Center = 2,
}

/// <summary>
/// 一块屏幕的壁纸信息。支持三种来源：
///   1. 文件路径（静态图片 / 幻灯片轮播 / Windows 聚焦在磁盘上的缓存文件）；
///   2. 内嵌 BMP 字节（TranscodedImageCache 内嵌数据，路径失效时的兜底）；
///   3. 均无效 → Path 为空，调用方保留上一张纹理（加载失败容错）。
/// </summary>
public sealed record WallpaperImageInfo(string? Path, byte[]? EmbeddedBmp,
                                       DateTime LastWriteUtc, WallpaperFillMode FillMode);

/// <summary>
/// 壁纸来源读取模块（模块①）。
///
/// 从注册表实时读取 Windows 当前桌面壁纸：
///   * 静态壁纸   → HKCU\Control Panel\Desktop\Wallpaper
///   * 幻灯片轮播 → HKCU\Control Panel\Desktop\TranscodedImageCache
///                  （二进制：头部 + UTF-16LE 图片路径 + 内嵌 BMP 数据）
///   * Windows 聚焦 → 同上缓存（聚焦图也在 TranscodedImageCache 中）
///   * 多显示器   → 主屏 TranscodedImageCache，扩展屏
///                  TranscodedImageCache_000 / _001 ...
/// </summary>
public static class WallpaperSource
{
    private static readonly Regex PathRegex = new(
        @"[A-Za-z]:\\[^\u0000\ud800-\udfff]{1,512}\.(jpg|jpeg|png|bmp|jfif)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>读取全部显示器的壁纸信息（按屏幕顺序）</summary>
    public static IReadOnlyList<WallpaperImageInfo> ReadAllMonitors(int monitorCount)
    {
        var result = new List<WallpaperImageInfo>(Math.Max(1, monitorCount));

        // 主屏
        result.Add(ReadCacheValue("TranscodedImageCache") ?? ReadStaticWallpaper());

        // 扩展屏：TranscodedImageCache_000、_001、...
        for (int i = 1; i < monitorCount; i++)
        {
            string name = i == 1 ? "TranscodedImageCache_000" : $"TranscodedImageCache_{i - 1:000}";
            result.Add(ReadCacheValue(name) ?? ReadStaticWallpaper());
        }

        return result;
    }

    /// <summary>读取指定 TranscodedImageCache 值并解析出路径/内嵌BMP</summary>
    private static WallpaperImageInfo? ReadCacheValue(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key == null) return null;
            var raw = key.GetValue(valueName) as byte[];
            if (raw == null || raw.Length < 12) return null;

            var (path, embedded) = ParseTranscoded(raw);

            // 主路径优先；文件不存在时退回内嵌 BMP（聚焦图缓存文件偶尔会被清理）
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return new WallpaperImageInfo(path, null, File.GetLastWriteTimeUtc(path), GetFillMode());

            if (embedded != null)
                return new WallpaperImageInfo(null, embedded, DateTime.MinValue, GetFillMode());

            return null;
        }
        catch
        {
            return null; // 注册表不可读：回退静态壁纸
        }
    }

    /// <summary>解析 TranscodedImageCache 二进制：路径（UTF-16LE）+ 内嵌 BMP</summary>
    private static (string? Path, byte[]? EmbeddedBmp) ParseTranscoded(byte[] data)
    {
        string? path = null;

        // 文件头（前 8 字节）之后是 UTF-16LE 的图片路径字符串（\0 结尾）
        if (data.Length > 16)
        {
            int maxChars = Math.Min((data.Length - 8) / 2, 2048);
            string s = Encoding.Unicode.GetString(data, 8, maxChars * 2);
            Match m = PathRegex.Match(s);
            if (m.Success)
                path = m.Value;
        }

        // 路径字符串终止后是内嵌 BMP（"BM" 魔数 0x42 0x4D，按 2 字节对齐扫描）
        byte[]? bmp = null;
        for (int i = 8; i < data.Length - 1; i += 2)
        {
            if (data[i] == 0x42 && data[i + 1] == 0x4D)
            {
                int len = data.Length - i;
                if (len >= 54) // 至少是合法 BMP 头
                {
                    bmp = new byte[len];
                    Array.Copy(data, i, bmp, 0, len);
                }
                break;
            }
        }

        return (path, bmp);
    }

    /// <summary>静态壁纸（HKCU\Control Panel\Desktop\Wallpaper）</summary>
    private static WallpaperImageInfo? ReadStaticWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? p = key?.GetValue("Wallpaper") as string;
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
            return new WallpaperImageInfo(p, null, File.GetLastWriteTimeUtc(p), GetFillMode());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取系统的填充方式设置：
    ///   WallpaperStyle: 0=居中 1=平铺 2=拉伸 6=适应 10=填充
    ///   TileWallpaper:  1=平铺
    /// 本程序把"适应/拉伸"统一映射为 Stretch、"平铺"映射为 Cover（网格渲染
    /// 以 Cover 为基准，视觉差异可接受），详见 MeshRenderer 注释。
    /// </summary>
    private static WallpaperFillMode GetFillMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("TileWallpaper") is string tile && tile == "1")
                return WallpaperFillMode.Cover;

            if (key?.GetValue("WallpaperStyle") is string style)
            {
                switch (style)
                {
                    case "0": return WallpaperFillMode.Center;
                    case "2": case "6": return WallpaperFillMode.Stretch;
                    default: return WallpaperFillMode.Cover; // 10=填充 及其它
                }
            }
        }
        catch { /* 忽略，用默认 Cover */ }

        return WallpaperFillMode.Cover;
    }
}
