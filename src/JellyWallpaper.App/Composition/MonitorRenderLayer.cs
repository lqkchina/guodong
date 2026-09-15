using System.Drawing;
using System.IO;
using System.Numerics;
using JellyWallpaper.App.Native;
using JellyWallpaper.App.Render;
using JellyWallpaper.App.Wallpaper;
using JellyWallpaper.Core.Config;
using JellyWallpaper.Core.Physics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.UI;
// 屏幕像素矩形（屏幕边界 / 布局计算）：显式别名消除歧义
// （System.Drawing.Rectangle vs System.Windows.Shapes.Rectangle）
using Rectangle = System.Drawing.Rectangle;

namespace JellyWallpaper.App.Composition;

/// <summary>
/// 单块屏幕的渲染层（模块④）：独立组合视觉 + 独立网格实例 + 独立绘制表面。
///
/// 每块屏幕拥有：
///   * CompositionDrawingSurface（DComp GPU 绘制表面，Direct2D 硬件加速绘制）
///   * CompositionSurfaceBrush + SpriteVisual（挂在桌面内容岛的根容器上）
///   * 独立 SpringMassGrid（该屏幕的物理网格，由物理线程步进，
///     本类只读其双缓冲快照 SnapX/SnapY —— 渲染与物理解耦）
///   * 当前壁纸纹理（加载失败保留上一张，不崩溃）
///
/// ── 渲染链路（Win2D 1.3.2 + WinAppSDK 1.6 官方互操作）────────────────
///   CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice)
///       ──→ CompositionGraphicsDevice（组合图形设备，绑定 CanvasDevice）
///   graphicsDevice.CreateDrawingSurface2(SizeInt32, fmt, alpha)
///       ──→ CompositionDrawingSurface（DComp 绘制表面）
///   CanvasComposition.CreateDrawingSession(surface) ──→ CanvasDrawingSession
///   每帧：清空 → 画基础壁纸层（cover 适配）→ 画形变网格层（MeshRenderer）
///   DComp 自动把表面合成到内容岛，无需显式 Present。
///
/// 渲染循环：合成线程上的 DispatcherQueueTimer（约 60Hz），与物理线程解耦。
/// </summary>
public sealed class MonitorRenderLayer : IDisposable
{
    /// <summary>屏幕包围矩形（物理像素，屏幕坐标）</summary>
    public Rectangle Bounds { get; }

    /// <summary>该屏幕 DPI</summary>
    public float Dpi { get; }

    /// <summary>
    /// 该屏幕的物理网格。由物理线程创建/步进（volatile 保证可见性）；
    /// 渲染线程只读取其 SnapX/SnapY 快照引用。
    /// </summary>
    public volatile SpringMassGrid? Grid;

    private readonly DesktopWallpaperHost _host;
    private readonly AppConfig _config;
    private readonly CanvasDevice _device;
    private readonly DispatcherQueue _queue;

    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private SpriteVisual? _visual;
    private CanvasBitmap? _wallpaper;
    private WallpaperImageInfo _source = new(null, null, DateTime.MinValue, WallpaperFillMode.Cover);
    private DispatcherQueueTimer? _timer;
    private bool _disposed;

    public MonitorRenderLayer(DesktopWallpaperHost host, AppConfig config,
                              CanvasDevice device, DispatcherQueue queue,
                              Rectangle bounds, float dpi)
    {
        _host = host;
        _config = config;
        _device = device;
        _queue = queue;
        Bounds = bounds;
        Dpi = dpi;
    }

    /// <summary>
    /// 在合成线程创建绘制表面 + 精灵视觉并启动渲染定时器。
    /// </summary>
    public void CreateVisual()
    {
        var compositor = _host.Compositor ?? throw new InvalidOperationException("宿主未初始化");
        var root = _host.Root ?? throw new InvalidOperationException("根容器未创建");

        // ① 组合图形设备：把 Win2D 的 CanvasDevice 绑定到组合合成器
        //   （WinAppSDK 1.6 中，绘制表面只能由 CompositionGraphicsDevice 创建）
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);

        // ② GPU 组合绘制表面（物理像素尺寸；B8G8R8A8 + 预乘 Alpha）
        _surface = _graphicsDevice.CreateDrawingSurface2(
            new Windows.Graphics.SizeInt32(Bounds.Width, Bounds.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        // ③ 表面画笔 → 精灵视觉（视觉尺寸/偏移用 DIP，与合成坐标一致）
        _brush = compositor.CreateSurfaceBrush();
        _brush.Surface = _surface;
        _brush.Stretch = CompositionStretch.Fill;

        _visual = compositor.CreateSpriteVisual();

        // 组合坐标是 DIP：物理像素 × (96/DPI)
        float dipScale = 96f / Dpi;
        _visual.Size = new Vector2(Bounds.Width * dipScale, Bounds.Height * dipScale);

        // 相对 Progman 窗口原点的偏移（Progman 通常覆盖主屏）
        var origin = _host.ProgmanOrigin();
        _visual.Offset = new Vector3(
            (Bounds.X - origin.X) * dipScale,
            (Bounds.Y - origin.Y) * dipScale, 0);

        _visual.Brush = _brush;
        root.Children.InsertAtTop(_visual);

        // 渲染循环：合成线程定时器，约 60Hz（与物理线程完全解耦）
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnRenderTick;
        _timer.Start();
    }

    /// <summary>
    /// 更新壁纸纹理（异步加载，不阻塞渲染循环；失败保留上一张）。
    /// 必须在合成线程调用。
    /// </summary>
    public void UpdateWallpaper(WallpaperImageInfo info)
    {
        _source = info;

        string? path = info.Path;

        // 路径失效但缓存里有内嵌 BMP → 落盘为临时文件再加载
        if (string.IsNullOrEmpty(path) && info.EmbeddedBmp != null)
        {
            path = WriteTempBmp(info.EmbeddedBmp);
        }

        if (string.IsNullOrEmpty(path))
        {
            // 无可加载来源：保留上一张正常纹理（容错要求）
            return;
        }

        var device = _device;
        var queue = _queue;
        string loadPath = path;

        // 后台线程加载图片 → 完成后回合成线程替换纹理
        _ = Task.Run(async () =>
        {
            CanvasBitmap? bmp = null;
            try
            {
                bmp = await CanvasBitmap.LoadAsync(device, loadPath).AsTask();
            }
            catch
            {
                // 图片读取失败：保留上一张正常纹理，不崩溃（模块①容错）
                return;
            }

            var captured = bmp;
            queue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    captured.Dispose();
                    return;
                }
                _wallpaper?.Dispose();
                _wallpaper = captured;
            });
        });
    }

    /// <summary>渲染一帧：基础壁纸层 + 形变网格层（DComp 表面，无需 Present）</summary>
    private void OnRenderTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _surface == null) return;

        using var ds = CanvasComposition.CreateDrawingSession(_surface);
        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0)); // 透明：露出系统壁纸

        var wallpaper = _wallpaper;
        var grid = Grid;

        if (wallpaper != null)
        {
            // 位图像素尺寸（BitmapSize → Windows.Foundation.Size）
            var bmpSize = new Windows.Foundation.Size(
                wallpaper.SizeInPixels.Width, wallpaper.SizeInPixels.Height);

            // 基础层：按系统的填充方式把壁纸完整画到整屏
            WallpaperLayout.ComputePlacement(bmpSize, Bounds.Size, _source.FillMode,
                                             out var dest, out var crop);
            ds.DrawImage(wallpaper, dest, crop, 1f, CanvasImageInterpolation.HighQualityCubic);
        }
        else
        {
            // 无纹理（不跟随系统壁纸 / 加载失败且无上一张）：纯色背景
            ds.Clear(ParseColor(_config.FallbackColorHex));
        }

        // 形变网格层：仅当有纹理、效果启用、且网格确有位移时才叠加绘制
        if (grid != null && _config.EffectEnabled && wallpaper != null &&
            grid.LastMaxDisplacement > 0.5f)
        {
            var bmpSize = new Windows.Foundation.Size(
                wallpaper.SizeInPixels.Width, wallpaper.SizeInPixels.Height);
            WallpaperLayout.ComputePlacement(bmpSize, Bounds.Size, _source.FillMode,
                                             out _, out var crop);
            MeshRenderer.Draw(ds, wallpaper, grid, crop);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnRenderTick;
        }
        _visual?.Dispose();
        _brush?.Dispose();
        _surface?.Dispose();
        _graphicsDevice?.Dispose();
        _wallpaper?.Dispose();
    }

    /// <summary>把内嵌 BMP 写为临时文件（TranscodedImageCache 缓存路径失效时的兜底）</summary>
    private static string? WriteTempBmp(byte[] bmpData)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "JellyWallpaper");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"wallpaper_{Environment.TickCount64}.bmp");
            File.WriteAllBytes(file, bmpData);
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 #RRGGBB 颜色；非法值回退到深色</summary>
    private static Windows.UI.Color ParseColor(string hex)
    {
        try
        {
            if (hex.StartsWith('#') && hex.Length == 7)
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
        }
        catch { /* 非法 → 默认 */ }
        return Windows.UI.Color.FromArgb(255, 30, 30, 46);
    }
}

/// <summary>壁纸在屏幕上的放置布局计算（Cover / Stretch / Center）</summary>
internal static class WallpaperLayout
{
    /// <summary>
    /// 计算"目标矩形 + 源裁剪矩形"（位图像素坐标）。
    /// Cover：等比缩放裁切铺满；Stretch：整图拉伸铺满；Center：原尺寸居中。
    /// </summary>
    public static void ComputePlacement(Windows.Foundation.Size bmp,
                                        System.Drawing.Size dest,
                                        WallpaperFillMode mode,
                                        out Windows.Foundation.Rect destRect,
                                        out Windows.Foundation.Rect srcRect)
    {
        // 注：Windows.Foundation.Size/Rect 的 Width/Height 在投影里是 double，
        //     统一转 float 参与计算
        float bw = (float)bmp.Width, bh = (float)bmp.Height;
        float dw = dest.Width, dh = dest.Height;

        if (bw <= 0 || bh <= 0)
        {
            destRect = new Windows.Foundation.Rect(0, 0, dw, dh);
            srcRect = new Windows.Foundation.Rect(0, 0, bw, bh);
            return;
        }

        switch (mode)
        {
            case WallpaperFillMode.Center when bw <= dw && bh <= dh:
                // 原尺寸居中，不拉伸
                destRect = new Windows.Foundation.Rect((dw - bw) / 2, (dh - bh) / 2, bw, bh);
                srcRect = new Windows.Foundation.Rect(0, 0, bw, bh);
                return;

            case WallpaperFillMode.Stretch:
                destRect = new Windows.Foundation.Rect(0, 0, dw, dh);
                srcRect = new Windows.Foundation.Rect(0, 0, bw, bh);
                return;

            default: // Cover（含 Center 但图比屏大的情况）
                float s = Math.Max(dw / bw, dh / bh); // 等比放大到铺满的倍数
                float cw = dw / s;                     // 取图宽度
                float ch = dh / s;                     // 取图高度
                destRect = new Windows.Foundation.Rect(0, 0, dw, dh);
                srcRect = new Windows.Foundation.Rect((bw - cw) / 2, (bh - ch) / 2, cw, ch);
                return;
        }
    }
}
