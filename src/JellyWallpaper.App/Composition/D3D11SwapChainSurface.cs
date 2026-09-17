using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using DeviceContext = SharpDX.Direct3D11.DeviceContext;

namespace JellyWallpaper.App.Rendering;

/// <summary>
/// DXGI 交换链渲染后端（v5.15 保底方案）。
///
/// ── 为什么存在 ──────────────────────────────────────────────────
/// 系统版 ICompositionDrawingSurfaceInterop.BeginDraw 在部分 Win10 上
/// 返回 S_OK 但不写回绘制对象指针（v5.9~v5.14 已坐实），该互操作不可靠。
/// 本类改用微软动态壁纸官方同款路径，完全绕开 BeginDraw：
///   1. DXGI Flip 交换链（CreateSwapChainForComposition，SharpDX 标准 API）
///   2. ICompositorInterop::CreateCompositionSurfaceForSwapChain
///      → CompositionSurfaceBrush + SpriteVisual（Composition 直接合成交换链内容）
///   3. 每帧：D3D11 顶点缓冲（物理网格形变位置）+ 壁纸纹理 + 最小 HLSL shader
///      → DrawIndexed → Present —— 全程 SharpDX 标准 D3D11，无任何黑魔法。
///
/// ── 网格映射 ────────────────────────────────────────────────────
/// 与物理引擎同口径：顶点数 = Cols × Rows（Cols=ceil(W/Cell)+1），
/// SnapX/SnapY[idx]（idx = row*Cols + col）为形变后的屏幕坐标（像素）。
/// 顶点位置 → NDC（x=px/W*2-1, y=1-py/H*2）；UV 取自未形变网格坐标
/// （col*Cell/texW, row*Cell/texH），与"1:1 像素映射"语义一致。
/// </summary>
public sealed class D3D11SwapChainSurface : IDisposable
{
    private Device _d3d = null!;
    private DeviceContext _ctx = null!;
    private SwapChain1 _swapChain = null!;
    private Texture2D _backBuffer = null!;
    private RenderTargetView _rtv = null!;
    private Texture2D? _wallpaperTex;
    private ShaderResourceView? _wallpaperSrv;
    private VertexShader _vs = null!;
    private PixelShader _ps = null!;
    private InputLayout _layout = null!;
    private Buffer _vertexBuffer = null!;
    private Buffer _indexBuffer = null!;
    private SamplerState _sampler = null!;

    private int _cols, _rows, _vertexCount, _indexCount;
    private int _screenW, _screenH;
    private float[] _vertexData = Array.Empty<float>();
    private int[] _indexData = Array.Empty<int>();
    private byte[]? _lastPixels;
    private ShaderResourceView? _fallbackSrv; // 壁纸未加载时的纯色兜底纹理
    private bool _disposed;
    private string _lastError = "";

    /// <summary>交换链原生指针（供 CreateCompositionSurfaceForSwapChain 使用）</summary>
    public IntPtr SwapChainPtr => _swapChain?.NativePointer ?? IntPtr.Zero;

    /// <summary>最近一次渲染/初始化错误（状态栏诊断）</summary>
    public string LastError => _lastError;

    /// <summary>壁纸纹理是否已加载（未加载时渲染纯色兜底）</summary>
    public bool HasWallpaper => _wallpaperTex != null;

    /// <summary>当前壁纸纹理尺寸（诊断显示）</summary>
    public int TexW { get; private set; }
    public int TexH { get; private set; }

    /// <summary>
    /// 初始化：创建 Composition 交换链 + D3D11 渲染资源。
    /// cols/rows = 物理网格顶点数（含 +1，与 SpringMassGrid 一致）。
    /// v5.16：逐步 try-catch，失败异常消息带步骤名，便于日志定位。
    /// </summary>
    public void Initialize(IntPtr d3dDevicePtr, int width, int height, int cols, int rows)
    {
        try
        {
            InitializeCore(d3dDevicePtr, width, height, cols, rows);
        }
        catch (Exception ex)
        {
            _lastError = "Initialize[" + ex.GetType().Name + "] " + ex.Message;
            Dispose();
            throw;
        }
    }

    private void InitializeCore(IntPtr d3dDevicePtr, int width, int height, int cols, int rows)
    {
        _screenW = width;
        _screenH = height;
        _cols = cols;
        _rows = rows;
        _vertexCount = cols * rows;
        _indexCount = (cols - 1) * (rows - 1) * 6;

        // 包装现有 D3D11 设备（共享 GPU 设备；不新建）
        try
        {
            _d3d = new Device(d3dDevicePtr);
            _ctx = _d3d.ImmediateContext;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("D3D11 设备包装失败 " + ex.Message, ex);
        }

        // ── ① DXGI 交换链（Flip 模型，Composition 专用）─────────────
        //    SharpDX 4.2.0 netstandard1.1 裁剪了 Factory2.CreateSwapChainForComposition，
        //    手写 vtable 槽位 24 调用（IDXGIFactory2 方法表，x64 全指针）。
        using (var factory2 = new Factory2())
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1_NATIVE
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = (uint)Format.B8G8R8A8_UNorm,
                Stereo = 0,
                SampleCount = 1,
                SampleQuality = 0,
                BufferUsage = (uint)Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = (uint)Scaling.Stretch,
                SwapEffect = (uint)SwapEffect.FlipSequential,
                AlphaMode = (uint)AlphaMode.Premultiplied,
                Flags = 0,
            };
            var dGch = System.Runtime.InteropServices.GCHandle.Alloc(desc, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                IntPtr swapChainPtr = IntPtr.Zero;
                int hr = RawDxgi.RawCreateSwapChainForComposition(
                    factory2.NativePointer, _d3d.NativePointer,
                    dGch.AddrOfPinnedObject(), IntPtr.Zero, out swapChainPtr);
                if (hr < 0 || swapChainPtr == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateSwapChainForComposition HRESULT=0x{hr:X8}");
                _swapChain = new SwapChain1(swapChainPtr);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("交换链创建失败 " + ex.Message, ex);
            }
            finally
            {
                dGch.Free();
            }
        }
        if (_swapChain == null)
            throw new InvalidOperationException("CreateSwapChainForComposition 返回 null");

        try
        {
            _backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
            _rtv = new RenderTargetView(_d3d, _backBuffer);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("后台缓冲/RTV 创建失败 " + ex.Message, ex);
        }

        // ── ② 最小 HLSL：顶点变换（NDC 直通）+ 纹理采样 ──────────────
        //   （无矩阵：位置已由物理网格在 CPU 转成 NDC；UV 来自网格原始坐标）
        const string hlsl = @"
Texture2D tex : register(t0);
SamplerState samp : register(s0);

struct VSInput { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct PSInput { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

PSInput VSMain(VSInput i)
{
    PSInput o;
    o.pos = float4(i.pos, 0.0f, 1.0f);
    o.uv = i.uv;
    return o;
}

float4 PSMain(PSInput i) : SV_Target
{
    return tex.Sample(samp, i.uv);
}
";
        try
        {
            var vsBytecode = ShaderBytecode.Compile(hlsl, "VSMain", "vs_4_0", ShaderFlags.None);
            var psBytecode = ShaderBytecode.Compile(hlsl, "PSMain", "ps_4_0", ShaderFlags.None);
            try
            {
                _vs = new VertexShader(_d3d, vsBytecode);
                _ps = new PixelShader(_d3d, psBytecode);
                _layout = new InputLayout(_d3d, vsBytecode, new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                });
            }
            finally
            {
                vsBytecode.Dispose();
                psBytecode.Dispose();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Shader 编译/创建失败 " + ex.Message, ex);
        }

        // ── ③ 顶点缓冲（Dynamic，每帧 Map 更新形变位置）＋ 索引缓冲 ──────
        //    D3D11 正统用法（v5.21 改为标准模式，消除驱动兼容问题）：
        //    · 索引缓冲 = IMMUTABLE + 创建时带初始数据（不可变缓冲必须如此）
        //    · 顶点缓冲 = DYNAMIC + Map(WriteDiscard) 每帧更新（见 UpdateVertices）
        try
        {
            _indexData = new int[_indexCount];
            BuildIndexData(); // 先填好索引数据
            var igch = System.Runtime.InteropServices.GCHandle.Alloc(
                _indexData, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                _indexBuffer = new Buffer(_d3d, igch.AddrOfPinnedObject(),
                    new BufferDescription(_indexCount * 4, ResourceUsage.Immutable,
                        BindFlags.IndexBuffer, CpuAccessFlags.None,
                        ResourceOptionFlags.None, 0));
            }
            finally
            {
                igch.Free();
            }
            _vertexBuffer = new Buffer(_d3d, new BufferDescription(
                _vertexCount * 16, ResourceUsage.Dynamic, BindFlags.VertexBuffer,
                CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("顶点/索引缓冲创建失败 " + ex.Message, ex);
        }

        // ── ④ 采样器（线性 + 边缘钳制，避免 UV 越界采样条纹）────────
        try
        {
            _sampler = new SamplerState(_d3d, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("采样器创建失败 " + ex.Message, ex);
        }

        // ── ⑤ 纯色兜底纹理（壁纸未加载时也能看到"果冻层"形变）──────
        //   4x4 深蓝灰 BGRA 像素，任意采样下都是同一颜色
        try
        {
            var fallbackPx = new byte[4 * 4 * 4];
            for (int i = 0; i < 4 * 4; i++)
            {
                fallbackPx[i * 4 + 0] = 0;       // B（亮绿诊断：壁纸纹理未参与绘制时显示）
                fallbackPx[i * 4 + 1] = 255;     // G
                fallbackPx[i * 4 + 2] = 0;       // R
                fallbackPx[i * 4 + 3] = 255;     // A
            }
            var fallbackTex = new Texture2D(_d3d, new Texture2DDescription
            {
                Width = 4, Height = 4, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
            var fgch = System.Runtime.InteropServices.GCHandle.Alloc(fallbackPx, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                _ctx.UpdateSubresource(fallbackTex, 0, null, fgch.AddrOfPinnedObject(), 4 * 4, 0);
            }
            finally
            {
                fgch.Free();
            }
            _fallbackSrv = new ShaderResourceView(_d3d, fallbackTex);
            fallbackTex.Dispose(); // SRV 已持有引用
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("兜底纹理创建失败 " + ex.Message, ex);
        }
    }

    /// <summary>
    /// 渲染一帧：更新网格顶点 → 上传壁纸纹理（变化时）→ 清屏 → 画网格 → Present。
    /// 全部在合成线程调用（D3D11 设备上下文不跨线程）。
    /// </summary>
    /// <param name="snapX/snapY">物理网格形变快照（长度 = cols*rows，可 null=未形变）</param>
    /// <param name="cols/rows">网格顶点列/行数（与 Initialize 一致）</param>
    /// <param name="cellSize">网格单元像素尺寸（UV 映射用）</param>
    /// <param name="pixels">壁纸 BGRA 像素（null = 纯色兜底）</param>
    public void Render(float[]? snapX, float[]? snapY, int cols, int rows, float cellSize,
                       byte[]? pixels, int texW, int texH, int screenW, int screenH,
                       int fitMode = 10)
    {
        if (_disposed || _swapChain == null) return;
        try
        {
            EnsureWallpaperTexture(pixels, texW, texH);
            UpdateVertices(snapX, snapY, cols, rows, cellSize, screenW, screenH, texW, texH, fitMode);

            _ctx.OutputMerger.SetRenderTargets(_rtv);
            // 清屏色：深蓝灰（合成层正常时被壁纸网格完全覆盖，仅纹理缺失时可见）
            _ctx.ClearRenderTargetView(_rtv, new RawColor4(0.08f, 0.10f, 0.14f, 1f));

            _ctx.InputAssembler.InputLayout = _layout;
            _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            _ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer, 16, 0));
            _ctx.InputAssembler.SetIndexBuffer(_indexBuffer, Format.R32_UInt, 0);
            _ctx.VertexShader.Set(_vs);
            _ctx.PixelShader.Set(_ps);
            _ctx.PixelShader.SetShaderResource(0, _wallpaperSrv ?? _fallbackSrv);
            _ctx.PixelShader.SetSampler(0, _sampler);
            _ctx.Rasterizer.SetViewport(new Viewport(0, 0, _screenW, _screenH, 0f, 1f));

            _ctx.DrawIndexed(_indexCount, 0, 0);
            // 0 = 不锁 vsync（由 60Hz 渲染定时器驱动，物理线程固定步长不受影响）
            _swapChain.Present(0, PresentFlags.None);
            _lastError = "";
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    /// <summary>更新顶点缓冲：物理网格形变位置 → NDC + UV</summary>
    /// <param name="fitMode">壁纸放置模式（0=居中 1=平铺[按拉伸] 2=拉伸 6=适应 10=填充/默认）</param>
    private void UpdateVertices(float[]? snapX, float[]? snapY, int cols, int rows,
                                float cellSize, int screenW, int screenH, int texW, int texH,
                                int fitMode = 10)
    {
        int vc = cols * rows;
        if (_vertexData.Length != vc * 4) _vertexData = new float[vc * 4];
        float invW = 2f / screenW, invH = 2f / screenH;
        float texWf = texW > 0 ? texW : screenW, texHf = texH > 0 ? texH : screenH;

        // ── UV 映射：把"壁纸源区域(srcX,srcY,srcW,srcH)"映射到整个屏幕 ──
        //    v5.23：按系统放置模式计算，大图不再 1:1 裁剪（此前只显示左上角）。
        //    10 填充/22 跨区=Cover：等比放大铺满，居中裁掉多余部分（系统默认）
        //    2  拉伸=Stretch：整图拉伸到屏幕（比例可能变形）
        //    6  适应=Contain：等比缩放完整可见（四周可能有黑边）
        //    0  居中=Center：原始像素尺寸，屏幕居中
        //    1  平铺：简化按拉伸处理（与 2 相同）
        float srcX = 0f, srcY = 0f, srcW = texWf, srcH = texHf;
        switch (fitMode)
        {
            case 0: // Center
                srcX = (texWf - screenW) / 2f;
                srcY = (texHf - screenH) / 2f;
                srcW = screenW;
                srcH = screenH;
                break;
            case 6: // Fit (Contain)
            {
                float scale = MathF.Min(texWf / screenW, texHf / screenH);
                srcW = texWf / scale;
                srcH = texHf / scale;
                srcX = (texWf - srcW) / 2f;
                srcY = (texHf - srcH) / 2f;
                break;
            }
            case 2: // Stretch（含平铺简化）
            case 1:
                srcX = 0; srcY = 0; srcW = texWf; srcH = texHf;
                break;
            default: // 10 Fill (Cover) + 22 跨区
            {
                // 关键修正（v5.25）：Cover 的缩放系数 = 壁纸/屏幕比例的"较小值"
                // （此前误用 Max → 源区域超过壁纸尺寸 → 大图只显示一部分/边缘拉伸）
                // scale = min(tw/sw, th/sh)，源区域 = 屏幕×scale ≤ 壁纸，
                // 居中裁掉多余 → 大图正确铺满整个屏幕。
                float scale = MathF.Min(texWf / screenW, texHf / screenH);
                srcW = screenW * scale;
                srcH = screenH * scale;
                srcX = (texWf - srcW) / 2f;
                srcY = (texHf - srcH) / 2f;
                break;
            }
        }
        float u0 = srcX / texWf, v0 = srcY / texHf;
        float uScale = srcW / texWf / screenW;   // 每屏幕像素对应的 UV 增量
        float vScale = srcH / texHf / screenH;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                int vi = idx * 4;
                float px = snapX != null && idx < snapX.Length ? snapX[idx] : c * cellSize;
                float py = snapY != null && idx < snapY.Length ? snapY[idx] : r * cellSize;
                // 屏幕像素 → NDC（D3D Y 轴向上，取反）
                _vertexData[vi] = px * invW - 1f;
                _vertexData[vi + 1] = 1f - py * invH;
                // UV：网格屏幕坐标 → 壁纸源区域映射
                _vertexData[vi + 2] = u0 + px * uScale;
                _vertexData[vi + 3] = v0 + py * vScale;
            }
        }
        // Dynamic 缓冲正统更新：Map(WriteDiscard) + 拷贝（避免驱动对
        // Dynamic+UpdateSubresource 组合的兼容问题，v5.21）
        var box = _ctx.MapSubresource(_vertexBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            var gch = System.Runtime.InteropServices.GCHandle.Alloc(
                _vertexData, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                SharpDX.Utilities.CopyMemory(box.DataPointer, gch.AddrOfPinnedObject(), vc * 16);
            }
            finally
            {
                gch.Free();
            }
        }
        finally
        {
            _ctx.UnmapSubresource(_vertexBuffer, 0);
        }
    }

    /// <summary>壁纸纹理：像素引用变化时才重建（BGRA8 → Texture2D → SRV）</summary>
    private void EnsureWallpaperTexture(byte[]? pixels, int w, int h)
    {
        if (pixels == null || w <= 0 || h <= 0) return;
        if (ReferenceEquals(pixels, _lastPixels)) return;

        _wallpaperSrv?.Dispose();
        _wallpaperTex?.Dispose();
        _wallpaperTex = null;
        _wallpaperSrv = null;

        var desc = new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };
        _wallpaperTex = new Texture2D(_d3d, desc);
        var gch = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            _ctx.UpdateSubresource(_wallpaperTex, 0, null, gch.AddrOfPinnedObject(), w * 4, 0);
        }
        finally
        {
            gch.Free();
        }
        _wallpaperSrv = new ShaderResourceView(_d3d, _wallpaperTex);
        TexW = w;
        TexH = h;
        _lastPixels = pixels;
    }

    /// <summary>构建三角形索引（网格单元 → 2 三角形），一次性上传</summary>
    private void BuildIndexData()
    {
        int k = 0;
        for (int r = 0; r < _rows - 1; r++)
        {
            for (int c = 0; c < _cols - 1; c++)
            {
                int i0 = r * _cols + c;
                int i1 = i0 + 1;
                int i2 = i0 + _cols;
                int i3 = i2 + 1;
                _indexData[k++] = i0; _indexData[k++] = i1; _indexData[k++] = i2;
                _indexData[k++] = i1; _indexData[k++] = i3; _indexData[k++] = i2;
            }
        }
        // 索引数据在 Initialize 创建 IMMUTABLE 缓冲时已随初始数据上传，无需再次上传
    }

    /// <summary>托管数组 → GPU 缓冲（pin 后 UpdateSubresource）</summary>
    private void Upload(Buffer target, Array data, int rowPitch)
    {
        var gch = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            _ctx.UpdateSubresource(target, 0, null, gch.AddrOfPinnedObject(), rowPitch, 0);
        }
        finally
        {
            gch.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler?.Dispose();
        _indexBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _layout?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _wallpaperSrv?.Dispose();
        _wallpaperTex?.Dispose();
        _fallbackSrv?.Dispose();
        _rtv?.Dispose();
        _backBuffer?.Dispose();
        _swapChain?.Dispose();
        _ctx?.Dispose();
        _d3d?.Dispose();
    }
}

/// <summary>
/// DXGI_SWAP_CHAIN_DESC1 原生布局（48 字节，x64）。
/// 字段顺序与 dxgitype.h 完全一致；全部按 UINT 排列避免 padding 歧义。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SWAP_CHAIN_DESC1_NATIVE
{
    public uint Width;           // 0   宽（像素）
    public uint Height;          // 4   高（像素）
    public uint Format;          // 8   DXGI_FORMAT
    public uint Stereo;          // 12  是否立体（Composition 交换链恒 false）
    public uint SampleCount;     // 16  DXGI_SAMPLE_DESC.Count
    public uint SampleQuality;   // 20  DXGI_SAMPLE_DESC.Quality
    public uint BufferUsage;     // 24  DXGI_USAGE（RenderTargetOutput = 0x20）
    public uint BufferCount;     // 28  后台缓冲数量（2 = 双缓冲 Flip）
    public uint Scaling;         // 32  DXGI_SCALING（Stretch = 0）
    public uint SwapEffect;      // 36  DXGI_SWAP_EFFECT（FlipSequential = 3）
    public uint AlphaMode;       // 40  DXGI_ALPHA_MODE（Premultiplied = 2）
    public uint Flags;           // 44  标志
}

/// <summary>手写调用 IDXGIFactory2::CreateSwapChainForComposition（vtable 槽位 24）</summary>
internal static partial class RawDxgi
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnCreateSwapChainForComposition(
        IntPtr factory, IntPtr device, IntPtr desc, IntPtr output, out IntPtr swapChain);

    /// <summary>
    /// 槽位 24：IDXGIFactory2 方法表第 25 项。
    /// 方法表索引（自 IUnknown 起）：0-2 IUnknown，3-6 IDXGIObject，
    /// 7-11 IDXGIFactory，12-13 IDXGIFactory1，14-23 IDXGIFactory2 前置方法，
    /// 24 = CreateSwapChainForComposition。
    /// </summary>
    public static int RawCreateSwapChainForComposition(
        IntPtr factory, IntPtr device, IntPtr descPtr, IntPtr output, out IntPtr swapChain)
    {
        swapChain = IntPtr.Zero;
        if (factory == IntPtr.Zero) return unchecked((int)0x80004003); // E_POINTER
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr fn = Marshal.ReadIntPtr(vtable, 24 * IntPtr.Size);
        var d = Marshal.GetDelegateForFunctionPointer<FnCreateSwapChainForComposition>(fn);
        return d(factory, device, descPtr, output, out swapChain);
    }
}
