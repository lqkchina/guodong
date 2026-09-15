using JellyWallpaper.Core.Physics;

namespace JellyWallpaper.Core.Tests;

/// <summary>
/// 弹簧-质点网格物理自测。
/// 全部断言失败时程序以非零退出码结束，供 CI 使用。
/// </summary>
internal static class Program
{
    private const double Dt = 1.0 / 60.0; // 固定步长 60 FPS
    private static int _failed;

    private static void Main()
    {
        Console.WriteLine("JellyWallpaper 物理引擎自测开始...\n");

        Test1_静止平衡();
        Test2_拖拽形变与位移钳制();
        Test3_松手回弹收敛();
        Test4_阻尼项功能();
        Test5_参数实时生效();
        Test6_网格重建();

        Console.WriteLine(_failed == 0
            ? "\n全部测试通过 ✔"
            : $"\n{_failed} 项测试失败 ✘");

        Environment.Exit(_failed == 0 ? 0 : 1);
    }

    /// <summary>断言辅助</summary>
    private static void Check(bool ok, string name, string detail = "")
    {
        if (ok)
        {
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}  {detail}");
        }
    }

    // 1) 无外力时网格应严格静止（内力自平衡）
    private static void Test1_静止平衡()
    {
        Console.WriteLine("Test1: 静止平衡（无外力不漂移、不震荡）");
        var p = new PhysicsParams();
        var g = new SpringMassGrid(640, 360, p);

        for (int i = 0; i < 120; i++) // 模拟 2 秒
            g.Step(Dt);

        Check(g.MaxDisplacementNow() < 0.01,
            "2 秒后无自发生位移", $"maxDisp={g.MaxDisplacementNow():F4}px");
        Check(g.TotalDisplacementEnergy() < 1e-4,
            "位移能量趋近零", $"energy={g.TotalDisplacementEnergy():E3}");
    }

    // 2) 鼠标拖拽：半径内质点被拉动，且位移不超过 MaxDisplacement
    private static void Test2_拖拽形变与位移钳制()
    {
        Console.WriteLine("Test2: 拖拽形变 + 最大位移钳制");
        var p = new PhysicsParams { MaxDisplacement = 100.0 };
        var g = new SpringMassGrid(640, 360, p);

        // 在屏幕中心按住鼠标拖 0.5 秒
        g.SetDrag(320, 180, true);
        for (int i = 0; i < 30; i++)
            g.Step(Dt);
        g.Release();

        double maxDisp = g.MaxDisplacementNow();
        Check(maxDisp > 10.0, "拖拽产生了明显形变", $"maxDisp={maxDisp:F1}px");
        Check(maxDisp <= p.MaxDisplacement + 0.5,
            "位移未超过钳制上限", $"maxDisp={maxDisp:F1}px <= {p.MaxDisplacement}px");
    }

    // 3) 松手后弹簧回弹，位移能量衰减到接近零（果冻回弹收敛）
    private static void Test3_松手回弹收敛()
    {
        Console.WriteLine("Test3: 松手回弹收敛（果冻效果）");
        var p = new PhysicsParams(); // 默认 Damping=0.25（欠阻尼，应有少量往复后收敛）
        var g = new SpringMassGrid(640, 360, p);

        g.SetDrag(320, 180, true);
        for (int i = 0; i < 30; i++) g.Step(Dt);
        g.Release();

        double e0 = g.TotalDisplacementEnergy();
        for (int i = 0; i < 600; i++) // 模拟 10 秒
            g.Step(Dt);

        double e1 = g.TotalDisplacementEnergy();
        Console.WriteLine($"    能量 {e0:F0} → {e1:F4}，最大位移 {g.MaxDisplacementNow():F3}px");
        Check(e1 < e0 * 0.01, "松手 10 秒后能量衰减超过 99%", $"e0={e0:F1} e1={e1:F4}");
        Check(g.MaxDisplacementNow() < 1.0, "质点基本回到原始位置", $"maxDisp={g.MaxDisplacementNow():F3}px");
    }

    // 4) 阻尼项功能验证：ζ≈0 时系统几乎不衰减（震荡），适中 ζ 时快速收敛。
    //    注意：过大的 ζ（>0.9）反而会使低频模式"蠕动"回位变慢（过阻尼真实物理），
    //    所以默认值取 0.25 附近的欠阻尼，兼顾 Q 弹与收敛速度。
    private static void Test4_阻尼项功能()
    {
        Console.WriteLine("Test4: 阻尼项功能（低阻尼不衰减 / 适中阻尼快速收敛）");

        // 4a) 低阻尼：4 秒后仍保留大量能量（弹簧系统在震荡，未被耗散殆尽）。
        //     注意：即使 ζ≈0，拖拽期间的"最大位移钳制/速度钳制"硬边界也会消耗部分能量，
        //     因此阈值取 30%（与 4b 的 5% 形成鲜明对比，足以证明阻尼项在耗能）。
        var pLow = new PhysicsParams { Damping = 0.001 };
        var gLow = new SpringMassGrid(640, 360, pLow);
        gLow.SetDrag(320, 180, true);
        for (int i = 0; i < 30; i++) gLow.Step(Dt);
        gLow.Release();
        double e0Low = gLow.TotalDisplacementEnergy();
        for (int i = 0; i < 240; i++) gLow.Step(Dt);
        double e4Low = gLow.TotalDisplacementEnergy();

        // 4b) 适中阻尼：4 秒后能量衰减到 5% 以下（快速回位）
        var pMid = new PhysicsParams { Damping = 0.45 };
        var gMid = new SpringMassGrid(640, 360, pMid);
        gMid.SetDrag(320, 180, true);
        for (int i = 0; i < 30; i++) gMid.Step(Dt);
        gMid.Release();
        double e0Mid = gMid.TotalDisplacementEnergy();
        for (int i = 0; i < 240; i++) gMid.Step(Dt);
        double e4Mid = gMid.TotalDisplacementEnergy();

        Check(e4Low > e0Low * 0.3,
            "低阻尼下 4 秒后仍保留 30% 以上能量（系统在震荡）", $"e/e0={e4Low / e0Low:P1}");
        Check(e4Mid < e0Mid * 0.05,
            "适中阻尼 4 秒内能量衰减超过 95%", $"e/e0={e4Mid / e0Mid:P1}");
    }

    // 5) 运行中修改参数立即生效（阻尼从 0.1 改为 0.95 后收敛速度明显变化）
    private static void Test5_参数实时生效()
    {
        Console.WriteLine("Test5: 参数实时修改立即生效");
        var p = new PhysicsParams { Damping = 0.1 };
        var g = new SpringMassGrid(640, 360, p);

        g.SetDrag(320, 180, true);
        for (int i = 0; i < 30; i++) g.Step(Dt);
        g.Release();

        // 前 1 秒：低阻尼，几乎没衰减
        for (int i = 0; i < 60; i++) g.Step(Dt);
        double eLowDamp = g.TotalDisplacementEnergy();

        // 中途把阻尼改成 0.95（模拟 UI 滑块实时拖动）
        p.Damping = 0.95;
        for (int i = 0; i < 120; i++) g.Step(Dt);
        double eHighDamp = g.TotalDisplacementEnergy();

        Check(eHighDamp < eLowDamp * 0.05,
            "改阻尼后收敛速度立即提升", $"e_低阻尼={eLowDamp:F1} → e_高阻尼={eHighDamp:F4}");
    }

    // 6) 修改网格尺寸后重建网格，且新网格初始即静止
    private static void Test6_网格重建()
    {
        Console.WriteLine("Test6: 网格密度重建");
        var p = new PhysicsParams();
        var g = new SpringMassGrid(640, 360, p);
        int oldCount = g.Cols * g.Rows;

        p.GridCellSize = 16.0; // 加密
        g.Rebuild();
        for (int i = 0; i < 60; i++) g.Step(Dt);

        Check(g.Cols * g.Rows > oldCount, "网格加密后质点数增加", $"{oldCount} → {g.Cols * g.Rows}");
        Check(g.MaxDisplacementNow() < 0.01, "重建后静止平衡", $"maxDisp={g.MaxDisplacementNow():F4}px");
    }
}
