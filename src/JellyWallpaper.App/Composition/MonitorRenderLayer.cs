using System.Drawing;
using System.IO;
using JellyWallpaper.App.Native;
using JellyWallpaper.App.Wallpaper;
using JellyWallpaper.Core.Config;
using JellyWallpaper.Core.Physics;
using SharpDX.WIC;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.System;
// ── 命名空间消歧（本工程同时启用 WPF + WinForms + SharpDX）──────────
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using D2DBitmap = SharpDX.Direct2D1.Bitmap;
using D2DContext = SharpDX.Direct2D1.DeviceContext;
using RawMatrix3x2 = SharpDX.Mathematics.Interop.RawMatrix3x2;
using RawRectangleF = SharpDX.Mathematics.Interop.RawRectangleF;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;

namespace JellyWallpaper.App.Composition;

/// <summary>
/// 单块屏幕的渲染层（模块④）：独立 Composition 视觉 + 独立 Direct2D
/// 绘制表面 + 独立网格实例。
///
/// ── 渲染链路（v4.5：系统版 Composition 表面 + SharpDX Direct2D）─────
///   表面：宿主 CompositionGraphicsDevice.CreateDrawingSurface
///        （系统版 API，Win10 10586+，Win10 19041 上稳定）
///   绘制：每帧 ICompositionDrawingSurfaceInterop.BeginDraw
///        → ID2D1DeviceContext（SharpDX 托管包装）
///        → 清屏 + 壁纸（静止：整图一次；形变：逐单元仿射变换）
///        → EndDraw 提交到合成表面
///   合成：CompositionSurfaceBrush + SpriteVisual 挂到宿主根容器
///        （Z 序：系统壁纸 → 本层 → 桌面图标）
///
/// ── 壁纸纹理 ─────────────────────────────────────────────────────
///   后台线程 WIC 解码（解码失败保留上一张）→ 像素数组 → 合成线程
///   渲染帧内用 ID2D1DeviceContext.CreateBitmapFromMemory 建 Direct2D
///   位图（位图绑定 Direct2D 设备，跨帧有效，避免 WIC 跨线程亲和问题）。
///
/// ── 渲染循环 ─────────────────────────────────────────────────────
///   合成线程 DispatcherQueueTimer 约 60Hz，与物理线程完全解耦；
///   网格快照（SnapX/SnapY）由物理线程写入，本层只读（volatile）。
/// </summary>
public sealed class MonitorRenderLayer : IDisposable
{
    /// <summary>屏幕包围矩形（物理像素，屏幕坐标）</summary>
    public Rectangle Bounds { get; }

    /// <summary>该屏幕 DPI</summary>
    public float Dpi { get; }

    /// <summary>
    /// 该屏幕的物理网格。由物理线程创建/步进；渲染线程只读
    /// SnapX/SnapY 快照与 LastMaxDisplacement。
    /// </summary>
    public volatile SpringMassGrid? Grid;

    private readonly DesktopWallpaperHost _host;
    private readonly AppConfig _config;
    private readonly DispatcherQueue _queue;

    // 合成对象（合成线程创建/使用）
    private CompositionDrawingSurface? _surface;
    private ICompositionDrawingSurfaceInterop? _surfaceInterop; // COM 视图
    private CompositionSurfaceBrush? _brush;
    private SpriteVisual? _visual;
    private DispatcherQueueTimer? _timer;

    // Direct2D 壁纸位图（绑定合成线程的 D2D 设备）
    private D2DBitmap? _wallpaperBmp;
    private WallpaperImageInfo _source = new(null, null, DateTime.MinValue, WallpaperFillMode.Cover);

    // 后台解码完成后的像素（合成线程渲染帧消费，引用比较避免重复建位图）
    private volatile byte[]? _pendingPixels;
    private volatile int _pendingW, _pendingH;
    private volatile WallpaperImageInfo _pendingSource = new(null, null, DateTime.MinValue, WallpaperFillMode.Cover);
    private byte[]? _loadedPixels; // 已建成位图的像素引用

    private bool _disposed;

    public MonitorRenderLayer(DesktopWallpaperHost host, AppConfig config,
                              DispatcherQueue queue, Rectangle bounds, float dpi)
    {
        _host = host;
        _config = config;
        _queue = queue;
        Bounds = bounds;
        Dpi = dpi;
    }

    /// <summary>
    /// 在合成线程创建绘制表面 + 画笔视觉并启动渲染定时器。
    /// </summary>
    public void CreateVisual()
    {
        var gd = _host.GraphicsDevice ?? throw new InvalidOperationException("宿主图形设备未初始化");
        var root = _host.Root ?? throw new InvalidOperationException("根容器未创建");
        var compositor = _host.Compositor ?? throw new InvalidOperationException("合成器未创建");

        // ① 绘制表面：系统级 Composition 表面，像素尺寸 = 屏幕尺寸。
        //    （预乘 alpha + BGRA8，与 Direct2D 默认格式一致）
        _surface = gd.CreateDrawingSurface(
            new Windows.Foundation.Size(Bounds.Width, Bounds.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        // ② 表面的 COM 视图（BeginDraw/EndDraw）
        //    （走 QueryInterface + RCW，不依赖 CsWinRT 内部 cast 行为）
        _surfaceInterop = CompositionInterop.GetInterop<ICompositionDrawingSurfaceInterop>(
            _surface, CompositionInterop.ICompositionDrawingSurfaceInteropIid);

        // ③ 表面画笔 + 精灵视觉：Stretch=None（1:1 像素），
        //    Offset = 屏幕原点 - Progman 原点（DComp 坐标系以 Progman 为原点）
        _brush = compositor.CreateSurfaceBrush(_surface);
        _brush.Stretch = CompositionStretch.None;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        _visual.Size = new System.Numerics.Vector2(Bounds.Width, Bounds.Height);

        var origin = _host.ProgmanOrigin();
        _visual.Offset = new System.Numerics.Vector3(
            Bounds.X - origin.X, Bounds.Y - origin.Y, 0);
        root.Children.InsertAtTop(_visual);

        // ④ 渲染循环：合成线程定时器约 60Hz（与物理线程解耦）
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnRenderTick;
        _timer.Start();
    }

    /// <summary>
    /// 更新壁纸（异步 WIC 解码，不阻塞渲染循环；失败保留上一张）。
    /// 必须在合成线程调用。
    /// </summary>
    public void UpdateWallpaper(WallpaperImageInfo info)
    {
        string? path = info.Path;

        // 路径失效但缓存里有内嵌 BMP → 落盘为临时文件再加载
        if (string.IsNullOrEmpty(path) && info.EmbeddedBmp != null)
            path = WriteTempBmp(info.EmbeddedBmp);

        if (string.IsNullOrEmpty(path))
            return; // 无可加载来源：保留上一张正常纹理（容错要求）

        string loadPath = path;
        var captured = info;

        // 后台线程：WIC 解码 → 32 位预乘 BGRA 像素数组（与 D2D 位图格式一致）
        _ = Task.Run(() =>
        {
            int w, h;
            byte[] pixels;
            try
            {
                using var factory = new ImagingFactory2();
                using var decoder = new BitmapDecoder(factory, loadPath, DecodeOptions.CacheOnDemand);
                using var frame = decoder.GetFrame(0);
                using var conv = new FormatConverter(factory);
                conv.Initialize(frame, PixelFormat.Format32bppPBGRA,
                                BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);
                w = conv.Size.Width;
                h = conv.Size.Height;
                if (w <= 0 || h <= 0) return;
                pixels = new byte[w * h * 4];
                conv.CopyPixels(pixels, w * 4);
            }
            catch
            {
                // 图片读取失败：保留上一张正常纹理，不崩溃（模块①容错）
                return;
            }

            // 完成 → 回合成线程：只更新"待消费像素"，不直接触碰渲染资源
            _queue.TryEnqueue(() =>
            {
                if (_disposed) return;
                _pendingPixels = pixels;
                _pendingW = w;
                _pendingH = h;
                _pendingSource = captured;
            });
        });
    }

    /// <summary>渲染一帧：BeginDraw → Direct2D 绘制 → EndDraw 提交合成</summary>
    private void OnRenderTick(DispatcherQueueTimer sender, object args)
    {
        var interop = _surfaceInterop;
        if (_disposed || interop == null) return;

        try
        {
            // ① BeginDraw：请求 ID2D1DeviceContext（整个表面更新）
            //    （static readonly IID 不能直接 ref，先拷贝到局部变量）
            var iid = CompositionInterop.IID_ID2D1DeviceContext;
            interop.BeginDraw(IntPtr.Zero, ref iid,
                              out IntPtr ctxPtr, out _);
            if (ctxPtr == IntPtr.Zero) return; // 设备丢失等：跳过本帧

            using (var ctx = new D2DContext(ctxPtr))
            {
                // ② 绘制（内部会先消费后台解码好的像素 → 建位图）
                var grid = Grid;
                DrawWallpaper(ctx, grid);
            }

            // ③ 提交到合成表面（EndDraw 后 BeginDraw 返回的 ctx 指针失效）
            interop.EndDraw();
        }
        catch
        {
            // 单帧失败（GPU 资源丢失/尺寸异常）：跳过本帧，不崩溃
        }
    }

    /// <summary>
    /// 绘制一帧壁纸。
    /// 静止（无位移）：整图一次 DrawBitmap —— 零开销；
    /// 形变中：逐网格单元仿射变换 DrawBitmap —— 果冻效果核心。
    /// </summary>
    private void DrawWallpaper(D2DContext ctx, SpringMassGrid? grid)
    {
        // 首次消费后台解码好的像素：用当前 ctx 建 Direct2D 位图
        //（位图绑定 D2D 设备，跨帧有效；引用比较避免重复重建）
        var pending = _pendingPixels;
        if (pending != null && !ReferenceEquals(pending, _loadedPixels))
        {
            try
            {
                BuildBitmap(ctx, pending, _pendingW, _pendingH, _pendingSource);
            }
            catch { /* 建位图失败：保留上一张 */ }
            _loadedPixels = pending;
        }

        var bmp = _wallpaperBmp;
        if (bmp == null)
        {
            ctx.Clear(ParseRawColor4(_config.FallbackColorHex));
            return;
        }

        // 源裁剪矩形（位图像素坐标，Cover/Stretch/Center 适配，与静止层对齐）
        WallpaperLayout.ComputePlacement(
            new SizeF(_wallpaperW, _wallpaperH), Bounds.Size,
            _source.FillMode, out _, out var crop);

        var srcFull = new RawRectangleF(crop.X, crop.Y,
                                        crop.X + crop.Width, crop.Y + crop.Height);

        // 静止（或网格未就绪）：整图一次（CPU/GPU 均最低开销）
        if (grid == null || grid.LastMaxDisplacement < 0.5f)
        {
            ctx.DrawBitmap(bmp, new RawRectangleF(0, 0, Bounds.Width, Bounds.Height),
                           1f, SharpDX.Direct2D1.BitmapInterpolationMode.Linear, srcFull);
            return;
        }

        // ── 形变中：逐单元仿射变换 ─────────────────────────────────
        // 力学背景：每个网格单元静止时对应纹理上 (ustep × vstep) 像素
        // 的源区域；形变后单元被弹簧质点拉成任意四边形。Direct2D 的
        // DrawBitmap 只支持仿射（平行四边形）变换，这里用单元内三顶点
        // （左上/右上/左下）确定仿射矩阵，把 [0,cell]² 映射到形变后的
        // 位置 —— 视觉上呈现平滑拉伸的果冻效果（v3 Win2D 版同款算法）。
        float cell = (float)grid.CellSize;
        int cols = grid.Cols, rows = grid.Rows;
        float us0 = crop.X, vs0 = crop.Y;
        float ustep = crop.Width / (float)grid.Width * cell;
        float vstep = crop.Height / (float)grid.Height * cell;

        float[] sx = grid.SnapX;
        float[] sy = grid.SnapY;
        // RawMatrix3x2 无静态 Identity，用单位矩阵字面量
        var identity = new RawMatrix3x2(1f, 0f, 0f, 1f, 0f, 0f);

        for (int r = 0; r < rows - 1; r++)
        {
            int rowBase = r * cols;
            int rowNext = rowBase + cols;
            float vsrc = vs0 + r * vstep;
            for (int c = 0; c < cols - 1; c++)
            {
                int i00 = rowBase + c;
                int i10 = i00 + 1;
                int i01 = rowNext + c;

                // 仿射矩阵：把 (x,y)∈[0,cell]² 映到
                //   左上(sx[i00],sy[i00])、右上(sx[i10],sy[i10])、
                //   左下(sx[i01],sy[i01]) 确定的平行四边形。
                //   矩阵元素 = 两相邻边的向量 / cell：
                //     M11 = Δx右/cell,  M12 = Δy右/cell  （x 轴基向量）
                //     M21 = Δx下/cell,  M22 = Δy下/cell  （y 轴基向量）
                //     M31 = 左上x,      M32 = 左上y       （平移）
                float m11 = (sx[i10] - sx[i00]) / cell;
                float m12 = (sy[i10] - sy[i00]) / cell;
                float m21 = (sx[i01] - sx[i00]) / cell;
                float m22 = (sy[i01] - sy[i00]) / cell;

                ctx.Transform = new RawMatrix3x2(m11, m12, m21, m22, sx[i00], sy[i00]);
                ctx.DrawBitmap(bmp,
                               new RawRectangleF(0, 0, cell, cell),
                               1f, SharpDX.Direct2D1.BitmapInterpolationMode.Linear,
                               new RawRectangleF(us0 + c * ustep, vsrc, ustep, vstep));
                ctx.Transform = identity;
            }
        }
    }

    /// <summary>用像素数组创建 Direct2D 位图（B8G8R8A8 预乘）</summary>
    private void BuildBitmap(D2DContext ctx, byte[] pixels, int w, int h, WallpaperImageInfo info)
    {
        var gch = System.Runtime.InteropServices.GCHandle.Alloc(pixels,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var props = new SharpDX.Direct2D1.BitmapProperties(
                new SharpDX.Direct2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                                                  SharpDX.Direct2D1.AlphaMode.Premultiplied));
            var bmp = new D2DBitmap(ctx, new SharpDX.Size2(w, h),
                                    new SharpDX.DataPointer(gch.AddrOfPinnedObject(), w * 4),
                                    w * 4, props);

            _wallpaperBmp?.Dispose();
            _wallpaperBmp = bmp;
            _wallpaperW = w;
            _wallpaperH = h;
            _source = info;
        }
        finally
        {
            gch.Free();
        }
    }

    private float _wallpaperW, _wallpaperH;

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

    /// <summary>解析 #RRGGBB 颜色；非法值回退到深色（Clear 底色用）</summary>
    private static RawColor4 ParseRawColor4(string hex)
    {
        try
        {
            if (hex.StartsWith('#') && hex.Length == 7)
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return new RawColor4(r / 255f, g / 255f, b / 255f, 1f);
            }
        }
        catch { /* 非法 → 默认 */ }
        return new RawColor4(30 / 255f, 30 / 255f, 46 / 255f, 1f);
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
        if (_surfaceInterop != null)
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(_surfaceInterop);
            _surfaceInterop = null;
        }
        _wallpaperBmp?.Dispose();
        _wallpaperBmp = null;
        _visual = null;
        _brush = null;
        _surface = null;
    }
}

/// <summary>壁纸在屏幕上的放置布局计算（Cover / Stretch / Center）</summary>
internal static class WallpaperLayout
{
    /// <summary>
    /// 计算"目标矩形 + 源裁剪矩形"（位图像素坐标）。
    /// Cover：等比缩放裁切铺满；Stretch：整图拉伸铺满；Center：原尺寸居中。
    /// </summary>
    public static void ComputePlacement(SizeF bmp,
                                        Size dest,
                                        WallpaperFillMode mode,
                                        out RectangleF destRect,
                                        out RectangleF srcRect)
    {
        float bw = bmp.Width, bh = bmp.Height;
        float dw = dest.Width, dh = dest.Height;

        if (bw <= 0 || bh <= 0)
        {
            destRect = new RectangleF(0, 0, dw, dh);
            srcRect = new RectangleF(0, 0, bw, bh);
            return;
        }

        switch (mode)
        {
            case WallpaperFillMode.Center when bw <= dw && bh <= dh:
                // 原尺寸居中，不拉伸
                destRect = new RectangleF((dw - bw) / 2, (dh - bh) / 2, bw, bh);
                srcRect = new RectangleF(0, 0, bw, bh);
                return;

            case WallpaperFillMode.Stretch:
                destRect = new RectangleF(0, 0, dw, dh);
                srcRect = new RectangleF(0, 0, bw, bh);
                return;

            default: // Cover（含 Center 但图比屏大的情况）
                float s = Math.Max(dw / bw, dh / bh); // 等比放大到铺满的倍数
                float cw = dw / s;                     // 取图宽度
                float ch = dh / s;                     // 取图高度
                destRect = new RectangleF(0, 0, dw, dh);
                srcRect = new RectangleF((bw - cw) / 2, (bh - ch) / 2, cw, ch);
                return;
        }
    }
}
