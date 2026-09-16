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
    // 路径匹配放宽：不再限定扩展名（Windows 聚焦的缓存文件无扩展名），
    // 匹配"盘符:\"开头的路径串，随后用 File.Exists 做存在性验证。
    private static readonly Regex PathRegex = new(
        @"[A-Za-z]:\\[^\u0000-\u001f\u007f]{3,512}",
        RegexOptions.Compiled);

    /// <summary>
    /// 最近一次读取的诊断摘要（UI 状态栏显示，定位"纹理=未加载"用）：
    /// 记录每块屏幕的来源（文件路径 / 内嵌BMP字节数 / 读取失败）。
    /// </summary>
    public static string LastReadInfo { get; private set; } = "尚未读取";

    /// <summary>读取全部显示器的壁纸信息（按屏幕顺序）</summary>
    public static IReadOnlyList<WallpaperImageInfo> ReadAllMonitors(int monitorCount)
    {
        var result = new List<WallpaperImageInfo>(Math.Max(1, monitorCount));
        var diag = new List<string>(Math.Max(1, monitorCount));

        // 主屏
        var main = ReadCacheValue("TranscodedImageCache") ?? ReadStaticWallpaper();
        diag.Add(Describe(main, "主屏"));
        if (main != null) result.Add(main);

        // 扩展屏：TranscodedImageCache_000、_001、...
        for (int i = 1; i < monitorCount; i++)
        {
            string name = i == 1 ? "TranscodedImageCache_000" : $"TranscodedImageCache_{i - 1:000}";
            var info = ReadCacheValue(name) ?? ReadStaticWallpaper();
            diag.Add(Describe(info, $"屏{i + 1}"));
            if (info != null) result.Add(info);
        }

        LastReadInfo = string.Join(" | ", diag);
        return result;
    }

    private static string Describe(WallpaperImageInfo? info, string label)
    {
        if (info == null) return $"{label}=读取失败";
        if (!string.IsNullOrEmpty(info.Path)) return $"{label}=文件:{Path.GetFileName(info.Path)}";
        if (info.EmbeddedBmp != null) return $"{label}=内嵌BMP:{info.EmbeddedBmp.Length}字节";
        return $"{label}=空";
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

        // 内嵌 BMP（"BM" 魔数）。格式在不同 Windows 版本略有差异，
        // 因此从 offset 0 起按 2 字节对齐扫描，并要求：
        //   'B''M' + 文件大小字段（第 2-5 字节，小端）≈ 剩余长度，
        //   且长度 ≥ 54（合法 BMP 头）—— 避免误中路径里的巧合字节。
        byte[]? bmp = null;
        for (int i = 0; i < data.Length - 54; i += 2)
        {
            if (data[i] != 0x42 || data[i + 1] != 0x4D) continue;
            int declared = data[i + 2] | (data[i + 3] << 8) |
                           (data[i + 4] << 16) | (data[i + 5] << 24);
            int remaining = data.Length - i;
            if (declared <= 0 || Math.Abs(declared - remaining) > 32) continue; // 长度对不上 → 伪 BM

            bmp = new byte[remaining];
            Array.Copy(data, i, bmp, 0, remaining);
            break;
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
