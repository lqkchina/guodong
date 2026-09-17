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

    /// <summary>桌面根窗口（枚举 WorkerW 用）</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    /// <summary>显示/隐藏窗口（SW_HIDE=0 / SW_SHOW=5）</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>窗口过程（自建壁纸窗口的消息处理，托管给 DefWindowProc）</summary>
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    // ── 桌面壁纸层挂载（v5.4：自建窗口挂入 WorkerW，不直接挂 Progman）──
    //   0x052C：让 Progman 创建壁纸承载层 WorkerW（经典动态壁纸机制，
    //   无数工具验证过：Win10/11 全系有效，无需注入与钩子）
    public const int WM_SPAWN_WORKERW = 0x052C;
    public const int SMTO_NORMAL = 0x0000;

    // 虚拟屏幕原点/尺寸（覆盖所有显示器）
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // 自建壁纸窗口风格
    public const uint WS_EX_NOACTIVATE = 0x08000000; // 点击不激活（不抢焦点）
    public const uint WS_EX_TRANSPARENT = 0x00000020; // 鼠标点击穿透（输入走轮询）
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_BOTTOM = new(1); // Z 序底部（图标层之下）

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
                                                   IntPtr lParam, int flags, int timeout,
                                                   out IntPtr result);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName,
                                          int nMaxCount);

    // ── 自建全屏壁纸窗口（Win32 窗口，托管给 DefWindowProc）────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName,
                                               string lpWindowName, uint dwStyle,
                                               int x, int y, int nWidth, int nHeight,
                                               IntPtr hWndParent, IntPtr hMenu,
                                               IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int x, int y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll")] // GetModuleHandle 属于 kernel32（误写 user32 会 EntryPointNotFoundException）
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);
}
