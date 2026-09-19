namespace JellyWallpaper.Core.Physics;

/// <summary>
/// 弹簧-质点模型中的单个质点。
///
/// 每个质点维护三组数据：
///   * RestX/RestY —— 原始（静止）坐标。弹簧的静止长度 L₀ 由相邻质点的
///     静止坐标差决定；锚点弹簧也以它为基准，保证松手后网格能整体回到原位。
///   * X/Y        —— 当前坐标（被弹簧力 / 拖拽外力驱动后的实时位置）。
///   * Vx/Vy      —— 当前速度（像素/秒）。物理积分只更新位置与速度两个状态量。
///   * SlowX/SlowY —— 黏弹性"肉记忆"参考点（v5.42 新增）：
///     模拟真人皮肤的黏弹性回弹——按压时它缓慢跟随形变（皮肤被压出
///     "记忆"），松开后它以较慢的时间常数恢复到原始坐标，让回弹呈
///     "先快后慢"的肉感（快速弹回大部分，剩余缓慢复位）。
///
/// 质点质量 m 归一化为 1，因此"力 = 加速度"，物理方程可以简化。
/// </summary>
public sealed class MassPoint
{
    /// <summary>原始（静止）坐标 X —— 弹簧网络的锚点</summary>
    public double RestX;

    /// <summary>原始（静止）坐标 Y</summary>
    public double RestY;

    /// <summary>当前坐标 X</summary>
    public double X;

    /// <summary>当前坐标 Y</summary>
    public double Y;

    /// <summary>速度 X（像素/秒）</summary>
    public double Vx;

    /// <summary>速度 Y（像素/秒）</summary>
    public double Vy;

    /// <summary>黏弹性"肉记忆"参考点 X（慢速锚点，见 SpringMassGrid.Step）</summary>
    public double SlowX;

    /// <summary>黏弹性"肉记忆"参考点 Y</summary>
    public double SlowY;

    public MassPoint(double x, double y)
    {
        RestX = X = SlowX = x;
        RestY = Y = SlowY = y;
    }

    /// <summary>
    /// 当前相对原始位置的位移长度：|P − P_rest|。
    /// 用于最大位移钳制（防止网格极端扭曲）。
    /// </summary>
    public double DisplacementLength()
    {
        double dx = X - RestX, dy = Y - RestY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>复位：回到原始坐标并清零速度（用于禁用效果 / 重建网格）</summary>
    public void Reset()
    {
        X = RestX;
        Y = RestY;
        SlowX = RestX;
        SlowY = RestY;
        Vx = 0;
        Vy = 0;
    }
}
