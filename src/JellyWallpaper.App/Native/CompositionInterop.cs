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
    public static readonly Guid ICompositorDesktopInteropIid = new("29E691FA-4567-4DCA-B319-DB0B3C6274D4");
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
}

/// <summary>
/// Compositor 桌面互操作接口（系统版 Windows.UI.Composition）。
/// MIDL_INTERFACE("29E691FA-4567-4DCA-B319-DB0B3C6274D4")
///   CreateDesktopWindowTarget(HWND, BOOL isTopmost, IDesktopWindowTarget** result);
///   EnsureOnThread(DWORD threadId);
/// CsWinRT 生成的 Compositor 对象实现 ICustomQueryInterface，
/// 可以直接 cast 到该 ComImport 接口（动态壁纸 C# 项目标准做法）。
/// </summary>
[ComImport]
[Guid("29E691FA-4567-4DCA-B319-DB0B3C6274D4")]
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
