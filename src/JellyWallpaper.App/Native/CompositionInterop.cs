using System.Drawing;
using System.Runtime.InteropServices;

namespace JellyWallpaper.App.Native;

/// <summary>
/// Windows.UI.Composition 原生互操作接口（v4.5 核心）。
///
/// 系统版 Composition（Windows.UI.Composition）的桌面挂载与图形设备
/// 创建都依赖这些"系统公开但 C# 投影未提供"的 COM 接口。三个接口的
/// GUID 与方法顺序取自 Windows SDK 头文件 windows.ui.composition.interop.h
/// （Windows 10 16299+，Win10 19041 上全部存在），逐一核对无误。
///
/// ── ICompositorDesktopInterop（GUID 29E691FA-...）───────────────────
///   Compositor 的桌面互操作：把 HWND（Progman）变成 CompositionTarget，
///   合成树从此渲染到桌面窗口客户区（壁纸→本层→图标，Z 序正确）。
///
/// ── ICompositorInterop（GUID 25297D5C-...）──────────────────────────
///   Compositor 的图形设备互操作：CreateGraphicsDevice(ID2D1Device)
///   创建 CompositionGraphicsDevice —— 这是创建绘制表面
///   （CompositionDrawingSurface）的唯一系统途径。
///
/// ── ICompositionDrawingSurfaceInterop（GUID FD04E6E3-...）───────────
///   绘制表面互操作：BeginDraw 返回 ID2D1DeviceContext（Direct2D 设备
///   上下文），用它画壁纸与形变网格，EndDraw 提交到合成表面。
/// </summary>
public static class CompositionInterop
{
    // ── 接口 IID（与头文件一致）─────────────────────────────────────
    public static readonly Guid ICompositorDesktopInteropIid = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    public static readonly Guid ICompositorInteropIid = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    public static readonly Guid ICompositionDrawingSurfaceInteropIid = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");

    // Direct2D 设备上下文 IID（ID2D1DeviceContext，标准 D2D 接口）
    public static readonly Guid IID_ID2D1DeviceContext = new("E8F7FE7A-191C-466D-AD95-975675BDA998");

    /// <summary>
    /// 从 CsWinRT WinRT 对象查询指定 COM 接口并返回 RCW。
    ///
    /// 不依赖 CsWinRT 生成类型是否实现 ICustomQueryInterface —— 直接
    /// 走底层：IWinRTObject.NativeObject → 原生指针 → Marshal.QueryInterface
    /// → Marshal.GetObjectForIUnknown（RCW，可直接调用 ComImport 接口方法）。
    /// 调用方负责在不再使用后 Marshal.ReleaseComObject(rcw) 释放 RCW。
    /// </summary>
    public static T GetInterop<T>(object winrtObject, Guid iid) where T : class
    {
        var native = ((WinRT.IWinRTObject)winrtObject).NativeObject.ThisPtr;
        int hr = Marshal.QueryInterface(native, ref iid, out IntPtr p);
        if (hr < 0 || p == IntPtr.Zero)
            throw new InvalidOperationException($"QueryInterface {iid} 失败: 0x{hr:X8}");
        try
        {
            var rcw = (T)Marshal.GetObjectForIUnknown(p);
            return rcw;
        }
        finally
        {
            Marshal.Release(p); // RCW 已持有自己的引用
        }
    }

    // ── 手写 vtable 调用（v5.11 核心）─────────────────────────────────
    // ComImport RCW 调用在部分环境会额外抛 .NET 异常（如 0x80131509，
    // InvalidOperationException 被 [PreserveSig] 转为 HRESULT 返回），
    // 无法确认"原生到底返回什么"。这里直接从 IUnknown vtable 槽位取
    // 函数指针调用 —— 零封送、零代理，返回 100% 原始 HRESULT。
    // ICompositionDrawingSurfaceInterop vtable 槽位（IUnknown 后）：
    //   [3]=BeginDraw  [4]=EndDraw  [5]=Resize  [6]=Scroll
    //   [7]=ResumeDraw [8]=SuspendDraw
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnBeginDraw(IntPtr self, IntPtr updateRect, IntPtr iid, out IntPtr updateObject, out POINTSTRUCT updateOffset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnEndDraw(IntPtr self);

    /// <summary>从任意 COM 接口指针手写调用 BeginDraw（槽位 3），返回原生 HRESULT</summary>
    public static int RawBeginDraw(IntPtr surfaceComPtr, IntPtr updateRect, Guid iid,
                                   out IntPtr updateObject, out POINTSTRUCT updateOffset)
    {
        updateObject = IntPtr.Zero;
        updateOffset = default;
        if (surfaceComPtr == IntPtr.Zero) return unchecked((int)0x80004003); // E_POINTER
        IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(surfaceComPtr), 3 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<FnBeginDraw>(fn);
        var gch = System.Runtime.InteropServices.GCHandle.Alloc(iid, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return del(surfaceComPtr, updateRect, gch.AddrOfPinnedObject(), out updateObject, out updateOffset);
        }
        finally
        {
            gch.Free();
        }
    }

    /// <summary>从任意 COM 接口指针手写调用 EndDraw（槽位 4），返回原生 HRESULT</summary>
    public static int RawEndDraw(IntPtr surfaceComPtr)
    {
        if (surfaceComPtr == IntPtr.Zero) return unchecked((int)0x80004003);
        IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(surfaceComPtr), 4 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<FnEndDraw>(fn);
        return del(surfaceComPtr);
    }
}

/// <summary>
/// Compositor 桌面互操作接口（系统版 Windows.UI.Composition）。
/// MIDL_INTERFACE("29E691FA-4567-4DCA-B319-D0F207EB6807")
///   CreateDesktopWindowTarget(HWND, BOOL isTopmost, IDesktopWindowTarget** result);
///   EnsureOnThread(DWORD threadId);
/// CsWinRT 生成的 Compositor 对象实现 ICustomQueryInterface，
/// 可以直接 cast 到该 ComImport 接口（动态壁纸 C# 项目标准做法）。
/// </summary>
[ComImport]
[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICompositorDesktopInterop
{
    /// <summary>
    /// 创建桌面窗口合成目标：把 HWND 变成 CompositionTarget，
    /// 之后把根视觉赋给 target.Root 即开始合成。
    /// </summary>
    /// <param name="hwndTarget">目标桌面窗口句柄（Progman）</param>
    /// <param name="isTopmost">false = 合成内容在窗口客户区内部（非顶层）</param>
    /// <param name="result">CompositionTarget（DesktopWindowTarget）的 IUnknown 指针</param>
    void CreateDesktopWindowTarget(IntPtr hwndTarget,
                                   [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
                                   out IntPtr result);

    /// <summary>确保互操作调用在指定线程执行（当前即合成线程，传自身线程 ID）</summary>
    void EnsureOnThread(uint threadId);
}

/// <summary>
/// Compositor 图形设备互操作接口。
/// MIDL_INTERFACE("25297D5C-3AD4-4C9C-B5CF-E36A38512330")
///   CreateCompositionSurfaceForHandle(HANDLE, ICompositionSurface**);
///   CreateCompositionSurfaceForSwapChain(IUnknown*, ICompositionSurface**);
///   CreateGraphicsDevice(IUnknown* renderingDevice, ICompositionGraphicsDevice** result);
/// </summary>
[ComImport]
[Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICompositorInterop
{
    void CreateCompositionSurfaceForHandle(IntPtr swapChain, out IntPtr result);
    void CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr result);

    /// <summary>
    /// 用底层 DirectX 设备（ID2D1Device）创建 CompositionGraphicsDevice。
    /// result 返回 ICompositionGraphicsDevice 指针，用
    /// Windows.UI.Composition.CompositionGraphicsDevice.FromAbi 包装。
    /// </summary>
    void CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr result);
}

/// <summary>
/// 绘制表面互操作接口（CompositionDrawingSurface 的 COM 视图）。
/// MIDL_INTERFACE("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE")
///   BeginDraw(RECT* updateRect, REFIID iid, void** updateObject, POINT* updateOffset);
///   EndDraw();
///   Resize(SIZE);
///   Scroll(RECT*, RECT*, int, int);
///   ResumeDraw();
///   SuspendDraw();
/// </summary>
[ComImport]
[Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICompositionDrawingSurfaceInterop
{
    /// <summary>
    /// 开始绘制：返回一个 Direct2D 设备上下文（按 iid 请求 ID2D1DeviceContext），
    /// 用它画图后调用 EndDraw 提交。
    /// </summary>
    /// <param name="updateRect">要更新的矩形（null = 全部），屏幕像素坐标</param>
    /// <param name="iid">请求的接口 IID（ID2D1DeviceContext）</param>
    /// <param name="updateObject">返回的接口指针</param>
    /// <param name="updateOffset">更新区域相对表面原点的偏移（调用方无需使用）</param>
    void BeginDraw(IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out Point updateOffset);

    /// <summary>提交绘制结果到合成表面</summary>
    void EndDraw();

    /// <summary>调整表面像素尺寸</summary>
    void Resize(Size sizePixels);

    void Scroll(IntPtr scrollRect, IntPtr clipRect, int offsetX, int offsetY);
    void ResumeDraw();
    void SuspendDraw();
}

/// <summary>
/// 绘制表面互操作接口的"裸调用"版本（[PreserveSig] + 原始 HRESULT）。
///
/// 用途：BeginDraw 失败时精确显示 HRESULT，并支持直接传 pinned Guid 指针
/// 排除 .NET ref Guid 封送的一切干扰。vtable 与系统版头文件一致。
/// </summary>
[ComImport]
[Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICompositionDrawingSurfaceInteropRaw
{
    /// <summary>返回 S_OK(0) 或负 HRESULT（E_INVALIDARG=0x80070057、DXGI_ERROR_DEVICE_REMOVED=0x887A0005 等）</summary>
    [PreserveSig] int BeginDraw(IntPtr updateRect, IntPtr iidPtr, out IntPtr updateObject, out POINTSTRUCT updateOffset);

    [PreserveSig] int EndDraw();

    [PreserveSig] int Resize(int width, int height);

    [PreserveSig] int Scroll(IntPtr scrollRect, IntPtr clipRect, int offsetX, int offsetY);

    [PreserveSig] int ResumeDraw();

    [PreserveSig] int SuspendDraw();
}

/// <summary>与 Win32 POINT 布局一致的结构（裸调用 out 参数用）</summary>
[StructLayout(LayoutKind.Sequential)]
public struct POINTSTRUCT
{
    public int X;
    public int Y;
}

/// <summary>与 Win32 RECT 布局一致的结构（裸调用 updateRect 参数用）</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECTSTRUCT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
