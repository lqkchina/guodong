using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using JellyWallpaper.App.Native;
using Windows.UI.Composition;
// 屏幕像素坐标点：显式别名消除歧义（System.Drawing.Point vs System.Windows.Point，
// 本工程同时启用 WPF 与 WinForms 时两个命名空间都可见，裸写 Point 会 CS0104）
using Point = System.Drawing.Point;

namespace JellyWallpaper.App.Composition;

/// <summary>
/// DWM 桌面合成宿主（模块④的核心，也是图层 Z 序的关键）。
///
/// ── 图层 Z 序原理（v5.4：自建窗口挂入桌面壁纸层）──────────────────
/// Windows 桌面的窗口结构（Win10/11）：
///     Progman（Program Manager，桌面根窗口）
///       ├─ SHELLDLL_DefView → SysListView32（桌面图标层）
///       └─ WorkerW（0x052C 消息后创建的壁纸承载层，位于图标之下）
///
/// 关键约束（实测坐实）：Composition 的 CreateDesktopWindowTarget **只允许
/// 挂载本进程创建的窗口**；挂 explorer 的 Progman 会返回 0x80070005
/// （E_ACCESSDENIED，拒绝访问）。因此正确做法是：
///   1. SendMessageTimeout(Progman, 0x052C) 让系统创建壁纸承载层 WorkerW；
///   2. 枚举顶层窗口，找到"不含 SHELLDLL_DefView 子窗口"的 WorkerW
///      （它正是图标之下的壁纸层）；
///   3. 本进程自建一个全屏 Win32 窗口（WS_EX_NOACTIVATE|WS_EX_TRANSPARENT，
///      输入走全局轮询，窗口不接收任何点击）；
///   4. SetParent(自建窗口, WorkerW) + SetWindowPos(HWND_BOTTOM)
///      → 自建窗口进入图标层之下、系统壁纸之上；
///   5. CreateDesktopWindowTarget(自建窗口, false) → CompositionTarget
///      （挂自己的窗口，权限通过；isTopmost=false 因为它是子窗口）。
///
/// 最终 Z 序严格为：系统壁纸 → 本程序 Direct2D 渲染层 → 桌面图标。
/// 且：不使用置顶透明窗口、不注入 explorer、不 Hook WorkerW、
/// 不装任何全局钩子（输入用 GetAsyncKeyState 轮询，见 MouseInputService）；
/// 图标层永远是独立 HWND，位于合成内容之上，移动/增删完全不受干扰。
///
/// ── 版本演进（为什么是 v5.4）──────────────────────────────────────
/// v1 WinAppSDK ContentIsland：Win10 19041 原生崩溃；
/// v2 WinAppSDK ICompositorDesktopInterop：C# 投影无 CompositionTarget；
/// v3 系统版 Composition + Win2D：Win2D 无系统版桌面资产；
/// v4 SharpDX.DirectComposition：NuGet 包残缺（核心方法未实现）；
/// v4.5 系统版挂载 Progman：GUID 拼错 → QI 0x80004002；
/// v5.3 修正 GUID 后：CreateDesktopWindowTarget 挂 Progman 被拒
///      （0x80070005）→ 坐实"只能挂自己的窗口"；
/// v5.4 自建壁纸窗口挂入 WorkerW，再挂载自己的窗口（本版本）。
///
/// ── 线程模型 ─────────────────────────────────────────────────────
/// 所有 Composition 对象必须在"带 DispatcherQueue 的合成线程"上创建
/// （见 WallpaperLayerManager），本类方法均在该线程调用。
/// </summary>
public sealed class DesktopWallpaperHost
{
    private const string HostWindowClass = "JellyWallpaperHostClass";

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CompositionTarget? _target;   // 保持强引用，防 GC 回收
    private CompositionGraphicsDevice? _graphicsDevice; // 保持强引用
    private IntPtr _hostHwnd = IntPtr.Zero;   // 自建的全屏壁纸窗口
    private IntPtr _layerHwnd = IntPtr.Zero;  // 壁纸层父窗口（WorkerW 或 Progman 兜底）
    private bool _initialized;
    private static bool _classRegistered; // 窗口类只需注册一次（进程级）

    /// <summary>是否已成功挂载到桌面</summary>
    public bool IsInitialized => _initialized;

    /// <summary>最近一次挂载失败的详细原因（UI 状态栏显示，用于快速定位）</summary>
    public string? LastError { get; private set; }

    /// <summary>组合根容器（子视觉按屏幕挂入）</summary>
    public ContainerVisual? Root => _root;

    /// <summary>合成器（各屏幕渲染层用它创建视觉/画笔）</summary>
    public Compositor? Compositor => _compositor;

    /// <summary>组合图形设备（各屏幕渲染层用它创建绘制表面）</summary>
    public CompositionGraphicsDevice? GraphicsDevice => _graphicsDevice;

    /// <summary>
    /// 挂载到桌面壁纸层。必须在合成线程调用。
    /// 返回 false 表示 explorer 尚未就绪或系统不支持（上层会定时重试）。
    /// 失败只降级不崩溃（全部挂载逻辑包在 try-catch 中）。
    /// 每一步失败都会把详细原因记录到 LastError（UI 状态栏显示）。
    /// </summary>
    /// <param name="d2dDevice">Direct2D 设备（SharpDX.Direct2D1.Device 的原生指针）
    /// —— CompositionGraphicsDevice 的底层渲染设备</param>
    public bool Initialize(IntPtr d2dDevice)
    {
        Teardown();
        LastError = null;

        try
        {
            // ① 找到桌面壁纸层父窗口（WorkerW；explorer 未就绪则重试）
            _layerHwnd = FindWallpaperLayer();
            if (_layerHwnd == IntPtr.Zero)
            {
                LastError = "找不到壁纸层窗口（explorer 未启动或桌面未就绪，持续重试中）";
                return false;
            }

            // ② 创建系统合成器（Windows.UI.Composition，Win10 自带）
            try
            {
                _compositor = new Compositor();
            }
            catch (Exception ex)
            {
                LastError = $"创建 Compositor 失败：{Describe(ex)}";
                return false;
            }
            _root = _compositor.CreateContainerVisual();

            // ③ 自建全屏壁纸窗口（本进程窗口，Composition 才能挂载）
            if (!CreateHostWindow())
                return false; // LastError 已在内部设置

            // ④ 把自建窗口挂进壁纸层（图标之下），并置于 Z 序底部
            if (!AttachToLayer())
                return false; // LastError 已在内部设置

            // ⑤ 桌面挂载：CreateDesktopWindowTarget(自建窗口, false)
            //    —— 挂"自己的窗口"权限必然通过；isTopmost=false（子窗口）。
            IntPtr targetPtr = IntPtr.Zero;
            ICompositorDesktopInterop? desktopInterop = null;
            try
            {
                desktopInterop = CompositionInterop.GetInterop<ICompositorDesktopInterop>(
                    _compositor, CompositionInterop.ICompositorDesktopInteropIid);
                desktopInterop.CreateDesktopWindowTarget(_hostHwnd, false, out targetPtr);
            }
            catch (Exception ex)
            {
                LastError = $"CreateDesktopWindowTarget 失败：{Describe(ex)}";
                return false;
            }
            finally
            {
                if (desktopInterop != null) Marshal.ReleaseComObject(desktopInterop);
            }
            if (targetPtr == IntPtr.Zero)
            {
                LastError = "CreateDesktopWindowTarget 返回空指针";
                return false;
            }

            // 包装为系统版投影对象（CompositionTarget 存在且带 Root 属性）
            try
            {
                _target = CompositionTarget.FromAbi(targetPtr);
                _target.Root = _root;
            }
            catch (Exception ex)
            {
                LastError = $"CompositionTarget.FromAbi 失败：{Describe(ex)}";
                return false;
            }
            finally
            {
                Marshal.Release(targetPtr); // FromAbi 已 AddRef，释放我们这一针
            }

            // ⑥ 图形设备：ICompositorInterop::CreateGraphicsDevice(ID2D1Device)
            //    → CompositionGraphicsDevice（创建绘制表面的来源）
            IntPtr gfxPtr = IntPtr.Zero;
            ICompositorInterop? compositorInterop = null;
            try
            {
                compositorInterop = CompositionInterop.GetInterop<ICompositorInterop>(
                    _compositor, CompositionInterop.ICompositorInteropIid);
                compositorInterop.CreateGraphicsDevice(d2dDevice, out gfxPtr);
            }
            catch (Exception ex)
            {
                LastError = $"CreateGraphicsDevice 失败：{Describe(ex)}";
                return false;
            }
            finally
            {
                if (compositorInterop != null) Marshal.ReleaseComObject(compositorInterop);
            }
            if (gfxPtr == IntPtr.Zero)
            {
                LastError = "CreateGraphicsDevice 返回空指针";
                return false;
            }

            try
            {
                _graphicsDevice = CompositionGraphicsDevice.FromAbi(gfxPtr);
            }
            catch (Exception ex)
            {
                LastError = $"CompositionGraphicsDevice.FromAbi 失败：{Describe(ex)}";
                return false;
            }
            finally
            {
                Marshal.Release(gfxPtr);
            }

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            // 未预期的兜底：记录后返回 false，由上层重试
            LastError = $"未预期异常：{Describe(ex)}";
            return false;
        }
        finally
        {
            if (!_initialized)
                Teardown(); // 失败路径清理半成品资源
        }
    }

    // ── 壁纸层窗口查找（经典 WorkerW 机制）────────────────────────────
    /// <summary>
    /// 让 Progman 创建壁纸承载层 WorkerW 并找到它。
    /// 0x052C 消息：Progman 收到后把图标（SHELLDLL_DefView）移入新的 WorkerW，
    /// 同时保留/创建另一个 WorkerW 作为壁纸层（在图标之下）。
    /// 枚举所有 "WorkerW" 顶层窗口，取"不含 SHELLDLL_DefView 子窗口"的那个
    /// —— 它就是壁纸层。找不到时兜底返回 Progman 本身。
    /// </summary>
    private static IntPtr FindWallpaperLayer()
    {
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // 触发 WorkerW 创建（对无窗口场景无害）
        NativeMethods.SendMessageTimeout(progman, NativeMethods.WM_SPAWN_WORKERW,
                                         IntPtr.Zero, IntPtr.Zero,
                                         NativeMethods.SMTO_NORMAL, 1000, out _);

        IntPtr workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "WorkerW") return true;

            // 含 SHELLDLL_DefView 子窗口的 WorkerW = 图标层（跳过）；
            // 不含的 = 壁纸承载层（目标）
            IntPtr icons = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons == IntPtr.Zero)
            {
                workerW = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return workerW != IntPtr.Zero ? workerW : progman; // 兜底：直接挂 Progman
    }

    // ── 自建壁纸窗口 ──────────────────────────────────────────────────
    /// <summary>
    /// 注册窗口类并创建覆盖整个虚拟桌面（所有显示器）的全屏窗口。
    /// 窗口不接收任何鼠标点击（WS_EX_TRANSPARENT 穿透 + 输入走全局轮询），
    /// 不抢焦点（WS_EX_NOACTIVATE），由 DefWindowProc 托管消息。
    /// </summary>
    private bool CreateHostWindow()
    {
        try
        {
            IntPtr hInst = NativeMethods.GetModuleHandle(null);
            if (!_classRegistered)
            {
                var wc = new NativeMethods.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInst,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = HostWindowClass,
                    hIconSm = IntPtr.Zero
                };
                if (NativeMethods.RegisterClassEx(ref wc) == 0)
                {
                    LastError = $"RegisterClassEx 失败：Win32 错误 {Marshal.GetLastWin32Error()}";
                    return false;
                }
                _classRegistered = true;
            }

            // 虚拟屏幕原点与尺寸（覆盖全部显示器）
            int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0)
            {
                LastError = "无法取得虚拟屏幕尺寸";
                return false;
            }

            uint exStyle = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
            _hostHwnd = NativeMethods.CreateWindowEx(
                exStyle, HostWindowClass, "JellyWallpaper",
                NativeMethods.WS_POPUP, // 先不显示：SetParent 进壁纸层后再显示，避免闪现
                vx, vy, vw, vh,
                IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hostHwnd == IntPtr.Zero)
            {
                LastError = $"CreateWindowEx 失败：Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"创建壁纸窗口失败：{Describe(ex)}";
            return false;
        }
    }

    /// <summary>
    /// 把自建窗口设为壁纸层（WorkerW）的子窗口，并置于 Z 序底部
    /// （SHELLDLL_DefView 图标层之下）。
    /// </summary>
    private bool AttachToLayer()
    {
        try
        {
            IntPtr prev = NativeMethods.SetParent(_hostHwnd, _layerHwnd);
            if (prev == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            {
                LastError = $"SetParent 失败：Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }

            // Z 序底部 + 重新定位全屏 + 显示（不激活）
            int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            if (!NativeMethods.SetWindowPos(_hostHwnd, NativeMethods.HWND_BOTTOM,
                                            vx, vy, vw, vh,
                                            NativeMethods.SWP_NOACTIVATE |
                                            NativeMethods.SWP_SHOWWINDOW))
            {
                LastError = $"SetWindowPos 失败：Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"挂入壁纸层失败：{Describe(ex)}";
            return false;
        }
    }

    /// <summary>默认窗口过程（静态委托需持有引用，防 GC）</summary>
    private static readonly NativeMethods.WndProcDelegate s_wndProc = WndProc;
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    /// <summary>把异常转成可读描述（含 HRESULT，便于定位）</summary>
    private static string Describe(Exception ex)
    {
        string msg = ex.Message;
        if (ex is COMException com)
            msg += $" (HRESULT 0x{com.HResult:X8})";
        return $"{ex.GetType().Name}: {msg}";
    }

    /// <summary>销毁旧 DWM 资源（explorer 重启后调用，随后重新 Initialize）</summary>
    public void Teardown()
    {
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
        _target?.Dispose();
        _target = null;
        _root = null;
        _compositor = null;
        _initialized = false;

        // 销毁自建壁纸窗口（explorer 重启后 WorkerW 已消亡，窗口可能已随
        // 父窗口销毁，DestroyWindow 失败属正常，容错忽略）
        if (_hostHwnd != IntPtr.Zero)
        {
            try { NativeMethods.DestroyWindow(_hostHwnd); } catch { /* 已销毁 */ }
            _hostHwnd = IntPtr.Zero;
        }
        _layerHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 壁纸层原点的屏幕坐标（用于把每块屏幕的视觉摆到正确位置）。
    /// 组合根的原点 = 自建窗口客户区原点 = 虚拟屏幕原点。
    /// </summary>
    public Point ProgmanOrigin()
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        return new Point(vx, vy);
    }
}
