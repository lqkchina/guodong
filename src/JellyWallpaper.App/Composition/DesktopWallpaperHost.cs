using System.Drawing;
using System.Runtime.InteropServices;
using JellyWallpaper.App.Native;
using Microsoft.UI.Composition;
// 屏幕像素坐标点：显式别名消除歧义（System.Drawing.Point vs System.Windows.Point，
// 本工程同时启用 WPF 与 WinForms 时两个命名空间都可见，裸写 Point 会 CS0104）
using Point = System.Drawing.Point;

namespace JellyWallpaper.App.Composition;

/// <summary>
/// DWM 桌面合成宿主（模块④的核心，也是图层 Z 序的关键）。
///
/// ── 图层 Z 序原理 ─────────────────────────────────────────────────
/// Windows 桌面的窗口结构（Win10/11）：
///     Progman（Program Manager，桌面根窗口）
///       ├─ SHELLDLL_DefView → SysListView32（桌面图标层，Progman 的子窗口）
///       └─ （DWM 绘制的系统壁纸层，位于 Progman 之下）
///
/// 本类使用 WinUI3 经典的桌面挂载方案（v2）：
///   ICompositorDesktopInterop + CompositionTarget
///   1. Compositor 创建根容器视觉；
///   2. 把 Compositor 转到 ICompositorDesktopInterop（WinAppSDK 公开的 COM 接口，
///      定义在 microsoft.ui.composition.interop.h），调用
///      CreateDesktopWindowTarget(Progman窗口, isTopmost:false) 获得 CompositionTarget；
///   3. 把根视觉赋给 CompositionTarget.Root，合成内容即渲染到桌面窗口客户区：
///      位于系统壁纸之上、图标子窗口（SysListView32）之下。
///
/// 最终 Z 序严格为：系统壁纸 → 本程序 Direct2D 渲染层 → 桌面图标。
/// 这正是需求要求的顺序，且：
///   * 不使用置顶透明窗口、不注入 explorer、不 Hook WorkerW、
///     不装任何全局钩子（输入用 GetAsyncKeyState 轮询，见 MouseInputService）；
///   * 图标层永远是独立 HWND，位于我们的合成内容之上，
///     移动/增删图标完全不受干扰，点击图标也走原生行为。
///
/// ── 为什么不用 ContentIsland/DesktopChildSiteBridge（v1）────────────
/// v1 在 Windows 10（如 19041）上实测触发 WinAppSDK 原生层的
/// AccessViolationException（DesktopChildSiteBridge.Create 崩溃）。
/// ICompositorDesktopInterop + CompositionTarget 是 WinAppSDK 1.0 时代至今的
/// 经典桌面挂载方案，在 Win10/Win11 上稳定性经过大量项目验证。
///
/// ── 线程模型 ─────────────────────────────────────────────────────
/// 所有 Composition 对象必须在"带 DispatcherQueue 的合成线程"上创建
/// （见 WallpaperLayerManager 的专用线程），本类方法均在该线程调用。
/// </summary>
public sealed class DesktopWallpaperHost
{
    // WinAppSDK 的 Compositor 桌面互操作 COM 接口（microsoft.ui.composition.interop.h）：
    //   MIDL_INTERFACE("29E691FA-4567-4DCA-B319-DB0B3C6274D4")
    //   ICompositorDesktopInterop : IUnknown {
    //     HRESULT CreateDesktopWindowTarget(HWND hwndTarget, BOOL isTopmost, IUnknown** result);
    //     HRESULT EnsureOnThread(DWORD threadId);
    //   };
    // CsWinRT 生成的 Compositor 对象实现了 ICustomQueryInterface，
    // 因此可以直接 cast 到该 ComImport 接口（WinUI3 C# 社区标准做法）。
    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-DB0B3C6274D4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        /// <summary>
        /// 创建桌面窗口合成目标：把 HWND 变成 CompositionTarget，
        /// 之后把根视觉赋给 target.Root 即开始合成。
        /// </summary>
        /// <param name="hwndTarget">目标桌面窗口句柄（Progman）</param>
        /// <param name="isTopmost">是否置顶；false = 合成内容在窗口客户区内部（非顶层）</param>
        /// <param name="result">返回 CompositionTarget 的 IUnknown 指针</param>
        void CreateDesktopWindowTarget(IntPtr hwndTarget,
                                       [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
                                       out IntPtr result);

        /// <summary>确保互操作调用在指定线程执行（当前即合成线程，传自身线程 ID）</summary>
        void EnsureOnThread(uint threadId);
    }

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CompositionTarget? _target; // 必须保持强引用，防止 GC 回收导致原生层崩溃
    private IntPtr _progmanHwnd;
    private bool _initialized;

    /// <summary>是否已成功挂载到桌面</summary>
    public bool IsInitialized => _initialized;

    /// <summary>组合根容器（子视觉按屏幕挂入）</summary>
    public ContainerVisual? Root => _root;

    /// <summary>合成器（各屏幕渲染层用它创建视觉/画笔）</summary>
    public Compositor? Compositor => _compositor;

    /// <summary>
    /// 查找 Progman 并创建桌面合成目标。必须在合成线程调用。
    /// 返回 false 表示 explorer 尚未就绪或系统不支持（WallpaperLayerManager 会定时重试）。
    /// 全部挂载逻辑包在 try-catch 中：失败只降级不崩溃，由上层重试。
    /// </summary>
    public bool Initialize()
    {
        Teardown();

        try
        {
            _progmanHwnd = NativeMethods.FindWindow("Progman", null);
            if (_progmanHwnd == IntPtr.Zero)
                return false; // explorer 未启动/重启中，稍后重试

            NativeMethods.GetWindowRect(_progmanHwnd, out NativeMethods.RECT progmanRect);

            _compositor = new Compositor();
            _root = _compositor.CreateContainerVisual();

            // Compositor → 桌面互操作接口（CsWinRT 对象支持 QI 到此自定义 COM 接口）
            var interop = (ICompositorDesktopInterop)(object)_compositor;

            // 挂到 Progman：isTopmost=false → 合成内容在桌面窗口客户区内部，
            // 位于系统壁纸之上、图标子窗口之下（Z 序正确）
            interop.CreateDesktopWindowTarget(_progmanHwnd, false, out IntPtr targetPtr);
            if (targetPtr == IntPtr.Zero)
            {
                Teardown();
                return false;
            }

            // 包装为 WinRT 投影对象（FromAbi 由 CsWinRT 生成），并设为根视觉
            _target = CompositionTarget.FromAbi(targetPtr);
            _target.Root = _root;

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            // 挂载失败（系统不支持/权限/原生崩溃等）：清理后返回 false，由上层重试。
            // 注意：.NET Core 可捕获 AccessViolationException，不会让进程直接死掉。
            System.Diagnostics.Debug.WriteLine($"[DesktopWallpaperHost] 挂载失败: {ex}");
            Teardown();
            return false;
        }
    }

    /// <summary>销毁旧 DWM 资源（explorer 重启后调用，随后重新 Initialize）</summary>
    public void Teardown()
    {
        _target?.Dispose();
        _target = null;
        _root = null;
        _compositor = null;
        _initialized = false;
        _progmanHwnd = IntPtr.Zero;
    }

    /// <summary>Progman 窗口的屏幕原点（用于把每块屏幕的视觉摆到正确位置）</summary>
    public Point ProgmanOrigin()
    {
        if (_progmanHwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_progmanHwnd, out NativeMethods.RECT r))
            return new Point(0, 0);
        return new Point(r.Left, r.Top);
    }
}
