using System.Drawing;
using JellyWallpaper.App.Native;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Content;
using Windows.Graphics;

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
/// 本类使用 Windows App SDK 1.6 的 ContentIsland + DesktopChildSiteBridge：
///   1. Compositor 创建根容器视觉；
///   2. ContentIsland.Create(根视觉) 建立内容岛；
///   3. DesktopChildSiteBridge.Create(compositor, WindowId(Progman窗口))
///      把岛挂载到桌面根窗口上，Connect + MoveAndResize + Show。
///
/// 内容岛的合成内容渲染在"Progman 自身内容之上、其子窗口（图标层）之下"，
/// 最终 Z 序严格为：系统壁纸 → 本程序 Direct2D 渲染层 → 桌面图标。
///
/// 这正是需求要求的顺序，且：
///   * 不使用置顶透明窗口、不注入 explorer、不 Hook WorkerW、
///     不装任何全局钩子（输入用 GetAsyncKeyState 轮询，见 MouseInputService）；
///   * 图标层永远是独立 HWND，位于我们的合成内容之上，
///     移动/增删图标完全不受干扰，点击图标也走原生行为。
///
/// ── 线程模型 ─────────────────────────────────────────────────────
/// 所有 Composition/Content 对象必须在"带 DispatcherQueue 的合成线程"上
/// 创建（见 WallpaperLayerManager 的专用线程），本类方法均在该线程调用。
/// </summary>
public sealed class DesktopWallpaperHost
{
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private ContentIsland? _island;
    private DesktopChildSiteBridge? _bridge;
    private IntPtr _progmanHwnd;
    private bool _initialized;

    /// <summary>是否已成功挂载到桌面</summary>
    public bool IsInitialized => _initialized;

    /// <summary>组合根容器（子视觉按屏幕挂入）</summary>
    public ContainerVisual? Root => _root;

    /// <summary>合成器</summary>
    public Compositor? Compositor => _compositor;

    /// <summary>
    /// 查找 Progman 并创建桌面内容岛。必须在合成线程调用。
    /// 返回 false 表示 explorer 尚未就绪或系统不支持（WallpaperLayerManager 会定时重试）。
    /// </summary>
    public bool Initialize()
    {
        Teardown();

        _progmanHwnd = NativeMethods.FindWindow("Progman", null);
        if (_progmanHwnd == IntPtr.Zero)
            return false; // explorer 未启动/重启中，稍后重试

        if (!DesktopSiteBridge.IsSupported())
            return false; // 系统不支持站点桥（Win10 1709+ 均支持，防御性检查）

        try
        {
            NativeMethods.GetWindowRect(_progmanHwnd, out NativeMethods.RECT progmanRect);

            _compositor = new Compositor();
            _root = _compositor.CreateContainerVisual();

            // 内容岛：根视觉即岛的根（WinAppSDK 1.6 API，替换旧版
            // ICompositorDesktopInterop/ICompositionTarget 的桌面目标方式）
            _island = ContentIsland.Create(_root);

            // 桌面子站点桥：把内容岛挂到 Progman 窗口上。
            // WindowId 由 HWND 指针值构造（与 WinAppSDK 官方 C++ 示例一致）。
            _bridge = DesktopChildSiteBridge.Create(_compositor, new WindowId((ulong)_progmanHwnd));
            _bridge.Connect(_island);

            // 让内容岛铺满 Progman 客户区（其子窗口图标层仍在岛的上方）
            _bridge.MoveAndResize(new RectInt32(0, 0, progmanRect.Width, progmanRect.Height));
            _bridge.Show();

            _initialized = true;
            return true;
        }
        catch
        {
            // 挂载失败（权限/资源等）：清理后返回 false，由上层重试
            Teardown();
            return false;
        }
    }

    /// <summary>销毁旧 DWM 资源（explorer 重启后调用，随后重新 Initialize）</summary>
    public void Teardown()
    {
        _bridge?.Dispose();
        _island?.Dispose();
        _bridge = null;
        _island = null;
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
