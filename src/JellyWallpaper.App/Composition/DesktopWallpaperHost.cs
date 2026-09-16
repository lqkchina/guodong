using System.Drawing;
using System.Runtime.InteropServices;
using JellyWallpaper.App.Native;
using Windows.UI.Composition;
// 屏幕像素坐标点：显式别名消除歧义（System.Drawing.Point vs System.Windows.Point，
// 本工程同时启用 WPF 与 WinForms 时两个命名空间都可见，裸写 Point 会 CS0104）
using Point = System.Drawing.Point;

namespace JellyWallpaper.App.Composition;

/// <summary>
/// DWM 桌面合成宿主（模块④的核心，也是图层 Z 序的关键）。
///
/// ── 图层 Z 序原理（v4.5：系统版 Windows.UI.Composition）────────────
/// Windows 桌面的窗口结构（Win10/11）：
///     Progman（Program Manager，桌面根窗口）
///       ├─ SHELLDLL_DefView → SysListView32（桌面图标层，Progman 的子窗口）
///       └─ （DWM 绘制的系统壁纸层，位于 Progman 之下）
///
/// 本类使用 Windows 10 系统内置的合成 API：
///   1. Compositor 创建根容器视觉（Windows.UI.Composition，系统自带）；
///   2. ICompositorDesktopInterop（系统公开 COM 接口，GUID 29E691FA-...）
///      CreateDesktopWindowTarget(Progman, false) → CompositionTarget
///      （系统版 C# 投影存在，v2 的 CS0246 问题不存在）；
///   3. ICompositorInterop（GUID 25297D5C-...）
///      CreateGraphicsDevice(ID2D1Device) → CompositionGraphicsDevice
///      —— 绘制表面（CompositionDrawingSurface）的创建来源；
///   4. 根视觉赋给 CompositionTarget.Root，合成内容渲染到桌面窗口
///      客户区：位于系统壁纸之上、图标子窗口（SysListView32）之下。
///
/// 最终 Z 序严格为：系统壁纸 → 本程序 Direct2D 渲染层 → 桌面图标。
/// 且：不使用置顶透明窗口、不注入 explorer、不 Hook WorkerW、
/// 不装任何全局钩子（输入用 GetAsyncKeyState 轮询，见 MouseInputService）；
/// 图标层永远是独立 HWND，位于合成内容之上，移动/增删完全不受干扰。
///
/// ── 版本演进（为什么是 v4.5）──────────────────────────────────────
/// v1 WinAppSDK ContentIsland：Win10 19041 原生崩溃；
/// v2 WinAppSDK ICompositorDesktopInterop：C# 投影无 CompositionTarget；
/// v3 系统版 Composition + Win2D：Win2D 无系统版桌面资产；
/// v4 SharpDX.DirectComposition：NuGet 包残缺（核心方法未实现）；
/// v4.5 系统版挂载（系统投影齐备）+ SharpDX Direct2D 渲染（包完整）。
///
/// ── 线程模型 ─────────────────────────────────────────────────────
/// 所有 Composition 对象必须在"带 DispatcherQueue 的合成线程"上创建
/// （见 WallpaperLayerManager），本类方法均在该线程调用。
/// </summary>
public sealed class DesktopWallpaperHost
{
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CompositionTarget? _target;   // 保持强引用，防 GC 回收
    private CompositionGraphicsDevice? _graphicsDevice; // 保持强引用
    private IntPtr _progmanHwnd;
    private bool _initialized;

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
    /// 查找 Progman 并创建桌面合成目标与图形设备。必须在合成线程调用。
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
            _progmanHwnd = NativeMethods.FindWindow("Progman", null);
            if (_progmanHwnd == IntPtr.Zero)
            {
                LastError = "找不到 Progman 窗口（explorer 未启动或桌面未就绪，持续重试中）";
                return false;
            }

            // ① 创建系统合成器（Windows.UI.Composition，Win10 自带）
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

            // ② 桌面挂载：Compositor → ICompositorDesktopInterop
            //    （系统版 Compositor 实现此接口；走 QueryInterface + RCW）
            IntPtr targetPtr = IntPtr.Zero;
            ICompositorDesktopInterop? desktopInterop = null;
            try
            {
                desktopInterop = CompositionInterop.GetInterop<ICompositorDesktopInterop>(
                    _compositor, CompositionInterop.ICompositorDesktopInteropIid);
                desktopInterop.CreateDesktopWindowTarget(_progmanHwnd, true, out targetPtr);
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

            // ③ 图形设备：ICompositorInterop::CreateGraphicsDevice(ID2D1Device)
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
