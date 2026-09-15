using System.Runtime.InteropServices;

namespace JellyWallpaper.App.Native;

/// <summary>
/// Windows API P/Invoke 集中声明。
/// 全部为"读取/查询"类 API：
///   * 查找桌面窗口（Progman / SHELLDLL_DefView / SysListView32）
///   * 鼠标状态轮询（GetCursorPos + GetAsyncKeyState，非钩子！）
///   * 图标包围矩形（ListView 消息）
///   * 每显示器 DPI
/// 不包含任何全局钩子、注入或窗口子类化 —— 与需求约束一致。
/// </summary>
internal static class NativeMethods
{
    // ── 窗口查找 ──────────────────────────────────────────────────────
    /// <summary>Program Manager 桌面窗口（所有桌面内容的根）</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    /// <summary>在父窗口下查找子窗口（用于定位桌面图标列表视图）</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
                                             string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ── 窗口矩形 ──────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // ── 鼠标（轮询式，非钩子）──────────────────────────────────────────
    public const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 查询按键状态（最上位 bit 表示是否按下）。注意：这是"查询"而不是
    /// 全局钩子，不需要注入任何进程，也不会触发杀软。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref RECT lpRect);

    // ── 桌面图标列表视图（ListView 消息，Shell API 兼容读取）───────────
    /// <summary>LVM_GETITEMCOUNT：图标数量</summary>
    public const int LVM_GETITEMCOUNT = 0x1004;

    /// <summary>LVM_GETITEMRECT：指定下标图标的包围矩形（客户区坐标）</summary>
    public const int LVM_GETITEMRECT = 0x100E;

    [DllImport("user32.dll", EntryPoint = "SendMessage")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessage")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

    // ── 每显示器 DPI ──────────────────────────────────────────────────
    public const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    public enum MonitorDpiType { Effective = 0, Angular = 1, Raw = 2 }

    [DllImport("Shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType,
                                              out uint dpiX, out uint dpiY);
}
