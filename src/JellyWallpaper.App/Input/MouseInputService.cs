using JellyWallpaper.App.Native;

namespace JellyWallpaper.App.Input;

/// <summary>一次鼠标采样结果</summary>
public readonly record struct MouseSample(int X, int Y, bool LeftDown);

/// <summary>
/// 鼠标输入服务（模块②的输入部分）。
///
/// 关键设计：本程序"不接收"也不拦截桌面鼠标消息（那是图标的领地），
/// 而是以 60Hz 轮询 GetCursorPos + GetAsyncKeyState 获取全局鼠标状态。
/// 这既满足"桌面空白拖拽"需求，又完全不触碰图标点击，并且——
/// 重要 —— 这不是钩子：不需要 SetWindowsHookEx / 全局钩子 / 注入，
/// 杀软零误报，explorer 零风险。
///
/// 图标命中判定在 SimulationLoop 中完成（命中图标 → 不触发形变）。
/// </summary>
public sealed class MouseInputService
{
    /// <summary>采样当前鼠标位置与左键状态（物理线程每帧调用，开销极小）</summary>
    public MouseSample Sample()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        bool leftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        return new MouseSample(pt.X, pt.Y, leftDown);
    }
}
