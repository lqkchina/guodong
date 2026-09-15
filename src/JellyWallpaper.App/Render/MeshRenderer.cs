using System.Numerics;
using JellyWallpaper.Core.Physics;
using Microsoft.Graphics.Canvas;

namespace JellyWallpaper.App.Render;

/// <summary>
/// 形变网格渲染器（模块④的网格部分，GPU 硬件加速）。
///
/// ── 原理 ────────────────────────────────────────────────────────────
/// 网格的每个单元四边形 = 两个三角形（三角网格）。为了让"被弹簧拉弯"的
/// 四边形正确显示壁纸内容，需要对每个四边形做 UV 纹理映射：
///   把壁纸源子矩形（该单元静止时覆盖的那块像素）映射到变形的四边形上。
///
/// 本实现使用 Direct2D 的仿射变换（Matrix3x2）近似该映射：
///   取变形四边形的三个顶点 (p00, p10, p01) 与源子矩形的三组对应点
///   (0,0)→p00、(w,0)→p10、(0,h)→p01 建立仿射矩阵：
///       M = [ (p10−p00)/w   (p01−p00)/h   p00 ]
///   然后 ds.Transform = M 后再 DrawImage(dest=(0,0,w,h), src=源子矩形)。
///
/// 说明：仿射映射是四边形（2 个三角形）线性映射的近似——第四个顶点 p11
/// 的映射误差 = p11 − (p00 + (p10−p00) + (p01−p00))（剪切残余）。
/// 弹簧场的形变是光滑连续的，网格单元足够密时该误差远小于像素，
/// 视觉上完全无缝；这也是多数"果冻/水波"类桌面效果的通用做法。
/// 若未来需要严格逐顶点 UV，可改用 Direct2D ID2D1Mesh +
/// ID2D1TessellationSink.AddTriangles 重建网格（接口层已隔离在此类）。
/// </summary>
internal static class MeshRenderer
{
    /// <summary>
    /// 把形变网格叠加绘制到交换链会话上。
    /// </summary>
    /// <param name="ds">绘制会话（transform 会被修改，结束时复位）</param>
    /// <param name="tex">壁纸纹理</param>
    /// <param name="grid">物理网格（读双缓冲快照）</param>
    /// <param name="srcRect">基础层使用的源裁剪矩形（位图像素坐标），
    /// 网格坐标↔纹理坐标的换算基准，保证网格层与基础层像素对齐</param>
    public static void Draw(CanvasDrawingSession ds, CanvasBitmap tex,
                            SpringMassGrid grid, Windows.Foundation.Rect srcRect)
    {
        int cols = grid.Cols;
        int rows = grid.Rows;
        if (cols < 2 || rows < 2) return;

        float[] sx = grid.SnapX;
        float[] sy = grid.SnapY;

        // 网格覆盖范围（屏幕像素）
        float gw = (float)grid.Width;
        float gh = (float)grid.Height;
        float cell = (float)grid.CellSize;

        // 源裁剪矩形 → 网格坐标的换算（cover 裁切后同样适用于网格层）
        // 注：Windows.Foundation.Rect 字段为 double，统一转 float 参与计算
        float us0 = (float)srcRect.X, vs0 = (float)srcRect.Y;
        float ustep = (float)srcRect.Width / gw * cell;  // 一个网格单元在纹理上的宽度
        float vstep = (float)srcRect.Height / gh * cell; // 一个网格单元在纹理上的高度

        // 逐单元绘制（四边形的两个三角形以一张仿射贴图近似）
        for (int r = 0; r < rows - 1; r++)
        {
            int rowBase = r * cols;
            int rowNext = (r + 1) * cols;
            for (int c = 0; c < cols - 1; c++)
            {
                int i00 = rowBase + c;
                int i10 = i00 + 1;
                int i01 = rowNext + c;

                float x00 = sx[i00], y00 = sy[i00];
                float e1x = sx[i10] - x00, e1y = sy[i10] - y00; // 上边向量
                float e2x = sx[i01] - x00, e2y = sy[i01] - y00; // 左边向量

                // 退化四边形（被拖成近零面积）跳过，避免花屏
                float area = Math.Abs(e1x * e2y - e1y * e2x);
                if (area < 1f) continue;

                // 3 点仿射矩阵（源 (0,0)/(w,0)/(0,h) → p00/p10/p01）
                float m11 = e1x / cell, m12 = e1y / cell;
                float m21 = e2x / cell, m22 = e2y / cell;

                ds.Transform = new Matrix3x2(m11, m12, m21, m22, x00, y00);
                ds.DrawImage(tex,
                             new Windows.Foundation.Rect(0, 0, cell, cell),
                             new Windows.Foundation.Rect(us0 + c * ustep, vs0 + r * vstep,
                                                          ustep, vstep),
                             1f, CanvasImageInterpolation.Linear);
            }
        }

        ds.Transform = Matrix3x2.Identity;
    }
}
