using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JellyWallpaper.App.Composition;
using JellyWallpaper.Core.Config;
using JellyWallpaper.Core.Physics;
using WinForms = System.Windows.Forms;

namespace JellyWallpaper.App;

/// <summary>
/// 设置 UI 面板（模块⑤）。
///
/// 所有滑条直接读写共享的 PhysicsParams 对象 —— 物理线程每帧读取最新值，
/// 因此"修改立即生效"，无需重启或重建任何东西（网格密度变化由
/// SimulationLoop 检测后自动 Rebuild）。
///
/// 关闭窗口 = 最小化到托盘（程序常驻），托盘菜单可重新显示设置或彻底退出。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly WallpaperLayerManager _manager;
    private WinForms.NotifyIcon? _tray;
    private bool _exiting;
    private bool _loading = true; // 初始加载滑块值时屏蔽事件

    public MainWindow(AppConfig config, WallpaperLayerManager manager)
    {
        InitializeComponent();

        _config = config;
        _manager = manager;

        LoadValuesFromConfig();
        SetupTray();

        _loading = false;

        // 状态栏定时刷新合成层挂载状态（explorer 重启后自动恢复显示）
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
    }

    /// <summary>把配置对象的值回填到所有控件（启动 / 恢复默认时调用）</summary>
    private void LoadValuesFromConfig()
    {
        PhysicsParams p = _config.Physics;
        SGrid.Value = Math.Clamp(p.GridCellSize, SGrid.Minimum, SGrid.Maximum);
        SStiffness.Value = Math.Clamp(p.Stiffness, SStiffness.Minimum, SStiffness.Maximum);
        SDamping.Value = Math.Clamp(p.Damping, SDamping.Minimum, SDamping.Maximum);
        SDragRadius.Value = Math.Clamp(p.DragRadius, SDragRadius.Minimum, SDragRadius.Maximum);
        SDragStrength.Value = Math.Clamp(p.DragStrength, SDragStrength.Minimum, SDragStrength.Maximum);
        SMaxDisp.Value = Math.Clamp(p.MaxDisplacement, SMaxDisp.Minimum, SMaxDisp.Maximum);

        CbEffect.IsChecked = _config.EffectEnabled;
        CbFollow.IsChecked = _config.FollowSystemWallpaper;

        RefreshLabels();
        UpdateStatus();
    }

    private void RefreshLabels()
    {
        PhysicsParams p = _config.Physics;
        LblGrid.Text = $"{p.GridCellSize:F0} px";
        LblStiffness.Text = $"{p.Stiffness:F0}";
        LblDamping.Text = $"{p.Damping:F2}";
        LblDragRadius.Text = $"{p.DragRadius:F0} px";
        LblDragStrength.Text = $"{p.DragStrength:F2}";
        LblMaxDisp.Text = $"{p.MaxDisplacement:F0} px";
    }

    private void UpdateStatus()
    {
        if (_manager.HostReady)
        {
            TxtStatus.Text = "合成层状态：已挂载到桌面（壁纸 → 果冻层 → 图标）";
            TxtStatusDetail.Text = "在桌面空白处按住左键拖拽即可看到果冻效果";
        }
        else
        {
            TxtStatus.Text = "合成层状态：等待 explorer 就绪…";
            // 显示具体失败原因（定位问题用）
            var err = _manager.LastInitError;
            TxtStatusDetail.Text = string.IsNullOrEmpty(err)
                ? "正在重试挂载…"
                : $"原因：{err}";
        }
    }

    // ── 滑条事件：写配置 + 刷新标签（立即生效）────────────────────────
    private void SGrid_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.GridCellSize = e.NewValue;
        LblGrid.Text = $"{e.NewValue:F0} px";
    }

    private void SStiffness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.Stiffness = e.NewValue;
        LblStiffness.Text = $"{e.NewValue:F0}";
    }

    private void SDamping_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.Damping = e.NewValue;
        LblDamping.Text = $"{e.NewValue:F2}";
    }

    private void SDragRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.DragRadius = e.NewValue;
        LblDragRadius.Text = $"{e.NewValue:F0} px";
    }

    private void SDragStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.DragStrength = e.NewValue;
        LblDragStrength.Text = $"{e.NewValue:F2}";
    }

    private void SMaxDisp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _config.Physics.MaxDisplacement = e.NewValue;
        LblMaxDisp.Text = $"{e.NewValue:F0} px";
    }

    // ── 开关事件 ──────────────────────────────────────────────────────
    private void CbEffect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.EffectEnabled = CbEffect.IsChecked == true;
    }

    private void CbFollow_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.FollowSystemWallpaper = CbFollow.IsChecked == true;
        // 立即按新开关重新应用壁纸/纯色
        _manager.RefreshWallpaper();
    }

    // ── 按钮事件 ──────────────────────────────────────────────────────
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _config.Save();
        TxtStatusDetail.Text = $"已保存到 {AppConfig.ConfigPath}";
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _config.Physics = new PhysicsParams();
        _loading = true;
        LoadValuesFromConfig();
        _loading = false;
        TxtStatusDetail.Text = "已恢复默认参数（配置尚未写入磁盘，点击“保存配置”持久化）";
    }

    // ── 托盘 ──────────────────────────────────────────────────────────
    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "果冻弹性壁纸",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>关闭窗口 = 隐藏到托盘（程序继续常驻）</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }
}