using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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
    private Rendering.D3D11SwapChainSurface? _d3dSurface; // v5.15 交换链渲染后端
    private ICompositionSurface? _compSurface;             // 交换链挂载的合成表面
    private CompositionSurfaceBrush? _brush;
    private SpriteVisual? _visual;
    private DispatcherQueueTimer? _timer;

    // 后台解码完成后的像素（合成线程渲染帧消费，引用比较避免重复建位图）
    private volatile byte[]? _pendingPixels;
    private volatile int _pendingW, _pendingH;
    private volatile WallpaperImageInfo _pendingSource = new(null, null, DateTime.MinValue, WallpaperFillMode.Cover);

    // 壁纸尺寸（诊断显示；解码成功后更新）
    private int _wallpaperW = 0, _wallpaperH = 0;

    private bool _disposed;

    // ── 诊断状态（UI 状态栏每秒显示，用于定位"拖拽无效果"）────────────
    private long _tickCount;              // 渲染帧累计
    private long _fpsWindowStart = Environment.TickCount64;
    private long _fpsWindowTicks;
    private float _renderFps;

    /// <summary>渲染帧率（最近 1 秒均值；0 = 渲染循环未跑/被跳过）</summary>
    public float RenderFps => _renderFps;

    /// <summary>最近一次渲染异常（BeginDraw/绘制失败时记录，不再静默吞掉）</summary>
    public string? LastRenderError { get; private set; }

    /// <summary>壁纸纹理状态（位图尺寸或"未加载"；未加载时渲染纯色背景）</summary>
    public string TextureInfo
    {
        get
        {
            if (DecodeError != null) return $"解码失败:{DecodeError}";
            return _wallpaperW <= 0 ? "未加载" : $"{_wallpaperW}x{_wallpaperH}";
        }
    }

    /// <summary>最近一次壁纸解码失败原因（后台线程写，诊断用）</summary>
    public volatile string? DecodeError;

    public MonitorRenderLayer(DesktopWallpaperHost host, AppConfig config,
                              DispatcherQueue queue, Rectangle bounds, float dpi, IntPtr ownerDevicePtr)
    {
        _host = host;
        _config = config;
        _queue = queue;
        Bounds = bounds;
        Dpi = dpi;
        _ownerDevicePtr = ownerDevicePtr;
    }

    /// <summary>所属 Composition 图形设备的底层 D3D11 设备指针（v5.15 交换链后端使用）</summary>
    private readonly IntPtr _ownerDevicePtr;

    /// <summary>本帧实际生效的绘制路径（状态栏诊断；v5.15 = SwapChain）</summary>
    private string _renderPath = "";

    /// <summary>实际生效的绘制路径（状态栏显示）</summary>
    public string RenderPath => _renderPath;

    /// <summary>
    /// 在合成线程创建绘制表面 + 画笔视觉并启动渲染定时器。
    /// v5.15：改用 DXGI 交换链后端（保底方案），完全绕开
    /// ICompositionDrawingSurfaceInterop.BeginDraw 互操作。
    /// </summary>
    public void CreateVisual()
    {
        var root = _host.Root ?? throw new InvalidOperationException("根容器未创建");
        var compositor = _host.Compositor ?? throw new InvalidOperationException("合成器未创建");

        // ① D3D11 交换链表面（网格顶点数 = 物理网格同口径）
        float cell = (float)_config.Physics.GridCellSize;
        int cols = (int)Math.Ceiling(Bounds.Width / cell) + 1;
        int rows = (int)Math.Ceiling(Bounds.Height / cell) + 1;
        _d3dSurface = new Rendering.D3D11SwapChainSurface();
        _d3dSurface.Initialize(_ownerDevicePtr, Bounds.Width, Bounds.Height, cols, rows);
        if (_d3dSurface.SwapChainPtr == IntPtr.Zero)
            throw new InvalidOperationException("交换链创建失败: " + _d3dSurface.LastError);

        // ② Composition 表面：交换链直挂（官方动态壁纸路径，无 BeginDraw）
        IntPtr surfacePtr = IntPtr.Zero;
        var compositorInterop = CompositionInterop.GetInterop<ICompositorInterop>(
            compositor, CompositionInterop.ICompositorInteropIid);
        try
        {
            compositorInterop.CreateCompositionSurfaceForSwapChain(_d3dSurface.SwapChainPtr, out surfacePtr);
        }
        finally
        {
            Marshal.ReleaseComObject(compositorInterop);
        }
        if (surfacePtr == IntPtr.Zero)
            throw new InvalidOperationException("CreateCompositionSurfaceForSwapChain 返回空指针");

        try
        {
            _compSurface = WinRT.MarshalInspectable<ICompositionSurface>.FromAbi(surfacePtr);
        }
        finally
        {
            Marshal.Release(surfacePtr); // FromAbi 已 AddRef
        }

        // ③ 表面画笔 + 精灵视觉：Stretch=None（1:1 像素），
        //    Offset = 屏幕原点 - Progman 原点（DComp 坐标系以 Progman 为原点）
        _brush = compositor.CreateSurfaceBrush(_compSurface);
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
            catch (Exception ex)
            {
                // 图片读取失败：保留上一张正常纹理，不崩溃（模块①容错），
                // 但记录原因到诊断（状态栏显示"纹理=解码失败:xxx"）
                string hr = ex switch
                {
                    System.Runtime.InteropServices.COMException com => $" 0x{com.HResult:X8}",
                    SharpDX.SharpDXException sd => $" 0x{(uint)sd.ResultCode:X8}",
                    _ => ""
                };
                DecodeError = $"解码失败 {ex.GetType().Name}:{ex.Message}{hr}";
                return;
            }

            DecodeError = null; // 解码成功
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

    /// <summary>渲染一帧：D3D11 交换链后端（v5.15，绕开 BeginDraw 互操作）</summary>
    private void OnRenderTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _d3dSurface == null) return;

        // 帧率统计（最近 1 秒均值，UI 状态栏显示）
        _tickCount++;
        long now = Environment.TickCount64;
        if (now - _fpsWindowStart >= 1000)
        {
            _renderFps = (_tickCount - _fpsWindowTicks) * 1000f / (now - _fpsWindowStart);
            _fpsWindowStart = now;
            _fpsWindowTicks = _tickCount;
        }

        try
        {
            // ① 物理网格快照（物理线程写、渲染线程只读）
            var grid = Grid;
            float[]? sx = grid?.SnapX;
            float[]? sy = grid?.SnapY;
            int cols = grid?.Cols ?? (int)Math.Ceiling(Bounds.Width / _config.Physics.GridCellSize) + 1;
            int rows = grid?.Rows ?? (int)Math.Ceiling(Bounds.Height / _config.Physics.GridCellSize) + 1;
            float cell = (float)_config.Physics.GridCellSize;

            // ② 壁纸像素（后台解码；未加载时传 null → 纯色兜底纹理）
            byte[]? px = _pendingPixels;
            int tw = _pendingW, th = _pendingH;
            if (px != null) { _wallpaperW = tw; _wallpaperH = th; }

            // ③ 渲染到交换链（内部：顶点更新 → 纹理 → 绘制 → Present）
            _d3dSurface.Render(sx, sy, cols, rows, cell, px, tw, th, Bounds.Width, Bounds.Height);
            _renderPath = "SwapChain";
            LastRenderError = _d3dSurface.LastError.Length > 0 ? "渲染失败 " + _d3dSurface.LastError : null;
        }
        catch (Exception ex)
        {
            // 渲染异常不崩溃：记录后继续下一帧
            LastRenderError = "渲染失败 " + DescribeException(ex);
        }
    }

    /// <summary>把异常转成诊断文本：类型 + 消息 + HRESULT + 第一个堆栈方法名</summary>
    private static string DescribeException(Exception ex)
    {
        string hr = ex switch
        {
            System.Runtime.InteropServices.COMException com => $" 0x{com.HResult:X8}",
            SharpDX.SharpDXException sd => $" 0x{(uint)sd.ResultCode:X8}",
            _ => ""
        };
        // 取堆栈里第一个"我们的渲染代码"帧的方法名（异常真正抛出的位置）
        string method = "";
        var sf = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("JellyWallpaper") || l.Contains("DrawWallpaper") || l.Contains("BuildBitmap") || l.Contains("D2D"));
        if (sf != null)
        {
            method = sf.Trim().TrimEnd('\r');
            if (method.Length > 120) method = method[..120];
        }
        return $"{ex.GetType().Name}: {ex.Message}{hr} @{method}";
    }

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
        System.Runtime.InteropServices.Marshal.ReleaseComObject(_compSurface);
        _d3dSurface?.Dispose(); // 内部释放交换链 / 纹理 / 缓冲 / shader
        _visual = null;
        _brush = null;
        _compSurface = null;
        _d3dSurface = null;
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
