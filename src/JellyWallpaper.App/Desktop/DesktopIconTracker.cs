using System.Drawing;
using JellyWallpaper.App.Native;
// 屏幕像素矩形：显式别名消除歧义（System.Drawing.Rectangle vs System.Windows.Shapes.Rectangle）
using Rectangle = System.Drawing.Rectangle;

namespace JellyWallpaper.App.Desktop;

/// <summary>
/// 桌面图标命中检测（模块③）。
///
/// 通过 Windows Shell API 读取桌面图标列表视图（SysListView32）的
/// 每个图标的屏幕包围矩形，供物理引擎判断"鼠标是否落在图标上"。
///
/// 实现要点（严格符合约束）：
///   * 只做"读取"：FindWindowEx 定位窗口 + SendMessage 发送
///     LVM_GETITEMCOUNT / LVM_GETITEMRECT（ListView 标准消息，跨进程合法）；
///   * 不注入 explorer、不子类化任何窗口、不安装钩子；
///   * 枚举所有顶层窗口下的 SHELLDLL_DefView → SysListView32，
///     因此主屏（Progman 下）与扩展屏（WorkerW 下）的图标都能覆盖。
///   * 结果缓存 500ms：每帧只需 O(1) 判断。
/// </summary>
public sealed class DesktopIconTracker
{
    private readonly object _gate = new();
    private List<Rectangle> _rects = new();
    private long _lastRefreshMs = long.MinValue;

    /// <summary>每 500ms 刷新一次图标矩形（桌面图标移动/增删在 0.5s 内反映）</summary>
    public void RefreshIfStale(int maxAgeMs = 500)
    {
        long now = Environment.TickCount64;
        if (now - _lastRefreshMs < maxAgeMs) return;
        _lastRefreshMs = now;

        var list = new List<Rectangle>(64);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            // 在顶层窗口下找 SHELLDLL_DefView（图标容器）
            IntPtr defView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;

            // 再找图标列表视图 SysListView32
            IntPtr listView = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (listView == IntPtr.Zero) return true;

            // 图标数量
            int count = (int)NativeMethods.SendMessage(listView, NativeMethods.LVM_GETITEMCOUNT,
                                                       IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count && i < 8192; i++)
            {
                var r = new NativeMethods.RECT();
                IntPtr ok = NativeMethods.SendMessage(listView, NativeMethods.LVM_GETITEMRECT,
                                                      (IntPtr)i, ref r);
                if (ok == IntPtr.Zero) continue;

                // 客户区坐标 → 屏幕坐标（GetCursorPos 同口径）
                NativeMethods.ClientToScreen(listView, ref r);
                list.Add(new Rectangle(r.Left, r.Top, r.Width, r.Height));
            }
            return true; // 继续枚举其余顶层窗口
        }, IntPtr.Zero);

        lock (_gate)
        {
            _rects = list;
        }
    }

    /// <summary>鼠标点 (x, y)（屏幕物理像素）是否落在任意图标矩形内</summary>
    public bool IsOverIcon(int x, int y)
    {
        lock (_gate)
        {
            foreach (var r in _rects)
            {
                if (r.Contains(x, y)) return true;
            }
        }
        return false;
    }
}
