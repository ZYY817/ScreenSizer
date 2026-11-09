using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenSizer.Controls;
using ScreenSizer.Models;
using ScreenSizer.Native;
using ScreenSizer.Services;
using Path = System.Windows.Shapes.Path;

namespace ScreenSizer;

public partial class MainWindow : Window
{
    private enum Mode { Screen, Window }
    private enum Page { Scale, Settings, About }

    /// <summary>署名 —— 改这里。</summary>
    public const string Author = "智宇丶817";

    private List<DisplayInfo> _displays = new();
    private readonly NvidiaScaler _nvidia;
    private readonly CcdScaler _ccd;
    private readonly WindowScaler _windowScaler = new();
    private IScreenScaler _scaler;
    private GpuService.Gpu _gpu = new(GpuVendor.Unknown, "未知");
    private Mode _mode = Mode.Screen;
    private Page _page = Page.Scale;
    private Window? _backdrop;
    private DispatcherTimer? _displayChangeTimer;
    private (int W, int H)? _resOverride;
    private AppSettings _settings = new();
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private HotkeyCombo _toggleHk, _forceHk;
    private int _captureId;
    private bool _statusError, _snapped;
    private bool _exiting, _ready, _mute;

    // 动画状态
    private double _gameScale = 1, _gameTarget = 1;
    private bool _gameAnimating;
    private double _mx, _my, _cx, _cy;
    private Tween? _inchTween, _toastTween, _aboutTween;
    private DispatcherTimer? _toastTimer, _confirmTimer;
    private int _confirmLeft;
    private readonly List<DispatcherTimer> _winTimers = new();
    private int _winStep = 3;
    private Storyboard? _glowStory;

    // 关于页视差：元素、所在屏、纵向速度、横向速度、横向基准
    private readonly List<(FrameworkElement El, int Sec, double Sy, double Sx, double X0)> _parallax = new();
    private int _aboutIndex;

    private static readonly double[] Presets = { 21.5, 23, 23.8, 24, 24.5, 25 };
    private const double ScreenW = 356, ScreenH = 200;

    public MainWindow()
    {
        InitializeComponent();
        _nvidia = new NvidiaScaler(Log);
        _ccd = new CcdScaler(Log);
        _scaler = _nvidia;
        Loaded += OnLoaded;
        Ruler.ValueChanged += (_, _) => { if (_ready) { _resOverride = null; UpdatePreview(); } };
        Ruler.PresetClicked += (_, v) => AnimateInch(v);
        WinRuler.PresetClicked += (_, v) => AnimateInch(v);
        GitHubLink.Tag = UpdateService.RepoUrl;
        IssuesLink.Tag = UpdateService.IssuesUrl;
        ReleasesLink.Tag = UpdateService.ReleasesUrl;
    }

    private bool Reduced => _settings.ReduceMotion ?? !SystemParameters.ClientAreaAnimation;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ApplyChrome(this);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        _settings = SettingsStore.Load();
        if (!HotkeyCombo.TryParse(_settings.HotkeyToggle, out _toggleHk) || _toggleHk.IsEmpty)
            _toggleHk = new HotkeyCombo(HotkeyService.MOD_CONTROL | HotkeyService.MOD_ALT, 0x53); // Ctrl+Alt+S
        if (!HotkeyCombo.TryParse(_settings.HotkeyForce, out _forceHk) || _forceHk.IsEmpty)
            _forceHk = new HotkeyCombo(HotkeyService.MOD_CONTROL | HotkeyService.MOD_ALT, 0x52); // Ctrl+Alt+R
        _hotkeys = new HotkeyService(hwnd, _toggleHk, _forceHk);
    }

    // ================= 标题栏 =================

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not ButtonBase) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimizeToTray && _tray != null)
        {
            e.Cancel = true;
            Hide();
            if (!_settings.TrayHintShown)
            {
                _settings.TrayHintShown = true;
                SettingsStore.Save(_settings);
                _tray.Balloon("AimFrame 在托盘待命", $"{_toggleHk} 应用 / 还原，右键托盘图标可退出");
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _backdrop?.Close();
        base.OnClosed(e);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // ================= 系统消息：分辨率变化 / 热键 =================

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            _displayChangeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _displayChangeTimer.Tick -= DisplayChangeSettled;
            _displayChangeTimer.Tick += DisplayChangeSettled;
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        }
        else if (msg == HotkeyService.WM_HOTKEY)
        {
            int id = w.ToInt32();
            if (id == HotkeyService.IdToggle) ToggleFromOutside();
            else if (id == HotkeyService.IdForce) ForceRestore(null, null);
            handled = true;
        }
        else if (msg == WM_DPICHANGED)
        {
            // DPI 变化（拖屏/改缩放）：颗粒按新 DPI 重建，刻度尺重绘
            Grain.Fill = MakeGrain(VisualTreeHelper.GetDpi(this).PixelsPerInchX);
            Ruler.InvalidateVisual();
            WinRuler.InvalidateVisual();
        }
        return IntPtr.Zero;
    }

    private void DisplayChangeSettled(object? sender, EventArgs e)
    {
        _displayChangeTimer?.Stop();
        ReloadDisplays();
        RefreshWindows(null, null);

        var wa = SystemParameters.WorkArea;
        if (Left + Width > wa.Right + 40 || Left < wa.Left - Width + 40 ||
            Top + Height > wa.Bottom + 40 || Top < wa.Top - 40)
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        UpdateAppliedUI();
        var st = StateStore.Load();
        var d = CurrentDisplay;
        if (_settings.DriftDetect && st != null && d != null &&
            (d.CurrentWidth != st.TargetW || d.CurrentHeight != st.TargetH))
            SetStatus(("分辨率被外部改为 ", false), ($"{d.CurrentWidth}×{d.CurrentHeight}", true), ("（游戏 / 系统）— 可点「还原全屏」恢复", false));
    }

    // ================= 初始化 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsStore.Load();
        ThemeService.Changed += OnThemeChanged;
        ThemeService.Apply(_settings.Theme, _settings.Accent);
        BuildSwatches();
        SyncSettingsUI();
        Grain.Fill = MakeGrain(VisualTreeHelper.GetDpi(this).PixelsPerInchX);
        RenderOptions.SetBitmapScalingMode(Grain, BitmapScalingMode.NearestNeighbor);
        AuthorRun.Text = Author;
        VerText.Text = UpdateService.CurrentText.ToUpperInvariant();
        UpdCur.Text = "当前 " + UpdateService.CurrentText;

        _gpu = GpuService.PrimaryGpu();
        bool nv = _gpu.Vendor == GpuVendor.Nvidia && NvApi.IsAvailable;
        bool ccd = _ccd.IsSupported;
        _scaler = nv ? _nvidia : _ccd;
#if DEBUG
        // 开发用：非 N 卡机器上也能预览「整个屏幕」界面（应用会失败并回滚，不会改动显示）
        bool uiDemo = Environment.GetEnvironmentVariable("SCREENSIZER_UI_DEMO") is "1" or "2";
#else
        const bool uiDemo = false;
#endif
#if DEBUG
        // 开发用：SCREENSIZER_UI_DEMO=2 时按 N 卡界面展示（精确尺寸、不吸附），仅用于截图，应用会失败并回滚
        if (Environment.GetEnvironmentVariable("SCREENSIZER_UI_DEMO") == "2") { uiDemo = true; nv = true; _scaler = _nvidia; _gpu = new GpuService.Gpu(GpuVendor.Nvidia, "NVIDIA GeForce RTX"); }
#endif
        string shortGpu = _gpu.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
        GpuText.Inlines.Clear();
        GpuText.Inlines.Add(new Run(nv ? "驱动级 · " : ccd ? "系统缩放 · " : "仅窗口模式 · "));
        GpuText.Inlines.Add(NumRun(shortGpu));
        if (!nv && !ccd && !uiDemo)
            GpuDot.SetResourceReference(Shape.FillProperty, "Text3Brush");
        SegScreen.IsEnabled = nv || ccd || uiDemo;

        _ready = true;
        Log($"启动 {UpdateService.CurrentText} · {_gpu.Name} · {(nv ? "驱动级缩放可用" : ccd ? "通用缩放（CCD 居中）可用" : "仅窗口模式")}");
        if (AppPaths.Migrated) Log("已从 ScreenSizer 迁移数据（旧目录保留，可手动删除）");
        if (AppPaths.MigrationError is { } me) Log("数据迁移失败，继续使用旧目录：" + me);
        if (StartupService.MigrateLegacy()) Log("开机自启项已从 ScreenSizer 更新为 AimFrame");
        ReloadDisplays();
        if (CurrentDisplay is { } d1)
            Log($"{d1.UiName} · 当前 {d1.CurrentWidth}×{d1.CurrentHeight} @ {d1.RefreshHz:0}Hz · 物理 {d1.DiagonalInches:0.0}″");
        RefreshWindows(null, null);
        SetMode(nv || ccd || uiDemo ? Mode.Screen : Mode.Window);

        var d0 = CurrentDisplay;
        if (d0 != null && d0.DiagonalInches >= 26.5) Ruler.Value = 24.5;
        BuildProfiles();
        UpdatePreview();
        _gameScale = _gameTarget;
        UpdateAppliedUI();

        RefreshHotkeyUI();

        _tray = new TrayService(ShowFromTray, () => ToggleFromOutside(forceApply: true), () => DoRestore(), ExitApp);
        _tray.SetAccent(ToDrawing(ThemeService.Accents[ThemeService.AccentIndex].Color));
        UpdateAppliedUI();

        CompositionTarget.Rendering += OnFrame;
        StartGlow();
        PlayIntro();
        After(3000, () => _ = AutoCheckUpdate());
#if DEBUG
        // 开发用：仅播放应用特效，验证动画路径（不改动显示设置）
        if (Environment.GetEnvironmentVariable("SCREENSIZER_FX_ONLY") == "1")
            After(1200, () => PlayApplyFx(0.8));
#endif
    }

    private void SyncSettingsUI()
    {
        _mute = true;
        ThemeLight.IsChecked = _settings.Theme == "light";
        ThemeDark.IsChecked = _settings.Theme == "dark";
        ThemeSystem.IsChecked = _settings.Theme is not ("light" or "dark");
        ReduceBox.IsChecked = Reduced;
        AutoStartBox.IsChecked = StartupService.IsAutoStartEnabled();
        TrayBox.IsChecked = _settings.MinimizeToTray;
        ConfirmBox.IsChecked = _settings.ConfirmCountdown;
        DriftDetectBox.IsChecked = _settings.DriftDetect;
        AutoUpdateBox.IsChecked = _settings.AutoUpdate;
        foreach (var r in SwatchPanel.Children.OfType<RadioButton>())
            r.IsChecked = (int)r.Tag == _settings.Accent;
        _mute = false;
    }

    private void OnThemeChanged()
    {
        _tray?.SetAccent(ToDrawing(ThemeService.Accents[ThemeService.AccentIndex].Color));
        if (_ready) { UpdatePreview(); UpdateAppliedUI(); }
    }

    private static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    // ================= 艺术层：颗粒 / 光晕 / 每帧视差 =================

    /// <summary>颗粒按物理像素 1:1 生成，避免缩放采样导致局部重绘时出现色块边界。</summary>
    private static ImageBrush MakeGrain(double dpi)
    {
        const int n = 160;
        var px = new byte[n * n * 4];
        var rnd = new Random(817);
        for (int i = 0; i < n * n; i++)
        {
            byte v = (byte)rnd.Next(0, 256);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = (byte)(rnd.Next(0, 2) == 0 ? 255 : 0);
        }
        var bmp = BitmapSource.Create(n, n, dpi, dpi, PixelFormats.Bgra32, null, px, n * 4);
        bmp.Freeze();
        double tile = n * 96.0 / dpi;
        var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, tile, tile), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        b.Freeze();
        return b;
    }

    private void StartGlow()
    {
        _glowStory?.Stop();
        GlowT.X = GlowT.Y = 0;
        if (Reduced) return;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var ax = new DoubleAnimation(0, -480, TimeSpan.FromSeconds(18)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var ay = new DoubleAnimation(0, 380, TimeSpan.FromSeconds(18)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(ax, Glow); Storyboard.SetTargetProperty(ax, new PropertyPath("RenderTransform.X"));
        Storyboard.SetTarget(ay, Glow); Storyboard.SetTargetProperty(ay, new PropertyPath("RenderTransform.Y"));
        sb.Children.Add(ax); sb.Children.Add(ay);
        sb.Begin();
        _glowStory = sb;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(RootGrid);
        _mx = p.X / Math.Max(1, RootGrid.ActualWidth) * 2 - 1;
        _my = p.Y / Math.Max(1, RootGrid.ActualHeight) * 2 - 1;
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e) { _mx = _my = 0; }

    private double _lastFrameScale = -1;

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _page != Page.Scale) return;

        // 预览画面尺寸：平滑跟随；收敛后吸附到目标，且只在变化时才改布局（避免每帧重排导致闪烁）
        if (!_gameAnimating)
        {
            _gameScale = Reduced ? _gameTarget : Tween.Lerp(_gameScale, _gameTarget, 0.22);
            if (Math.Abs(_gameScale - _gameTarget) < 0.0005) _gameScale = _gameTarget;
        }
        if (Math.Abs(_gameScale - _lastFrameScale) > 1e-6)
        {
            SetGameFrame(_gameScale);
            _lastFrameScale = _gameScale;
        }

        // 鼠标视差：静止后不再写变换
        double tx = Reduced ? 0 : _mx, ty = Reduced ? 0 : _my;
        if (Math.Abs(_cx - tx) < 0.0005 && Math.Abs(_cy - ty) < 0.0005) return;
        _cx = Tween.Lerp(_cx, tx, 0.08); _cy = Tween.Lerp(_cy, ty, 0.08);
        Shift(LySky, 8); Shift(LyMtn, 18); Shift(LyBld, 34); Shift(LyGnd, 56);
        GhostT.X = _cx * 16; GhostT.Y = _cy * 10;
        SheenT.X = _cx * 30; SheenT.Y = _cy * 8;
    }

    private void Shift(FrameworkElement layer, double depth)
    {
        if (layer.RenderTransform is TranslateTransform t) { t.X = -_cx * depth; t.Y = -_cy * depth * 0.5; }
    }

    private void SetGameFrame(double s)
    {
        GameFrame.Width = Math.Max(1, ScreenW * s);
        GameFrame.Height = Math.Max(1, ScreenH * s);
        double dw = DimsPanel.ActualWidth > 0 ? DimsPanel.ActualWidth : ScreenW - 10;
        DimInner.Width = Math.Max(20, dw * Math.Min(1, s));
        DimInner.Margin = new Thickness(dw * (1 - Math.Min(1, s)) / 2, 0, 0, 0);
    }

    // ================= 启动动画 =================

    private void PlayIntro()
    {
        if (Reduced) { RevealInstant(); return; }
        IntroLayer.Visibility = Visibility.Visible;
        IntroLayer.Opacity = 1;
        IntroScale.ScaleX = IntroScale.ScaleY = 1;
        foreach (var p in IntroCorners) { p.Opacity = 0; p.SetResourceReference(Shape.StrokeProperty, "TextBrush"); }
        IntroMark.Opacity = 0; IntroScan.Opacity = 0; IntroCount.Opacity = 0;
        IntroNum.Text = "27";
        foreach (var el in RevealTargets) el.Opacity = 0;

        RootGrid.UpdateLayout();
        double W = RootGrid.ActualWidth, H = RootGrid.ActualHeight;
        var from = new Rect(22, 22, W - 44, H - 44);
        var to = new Rect(W / 2 - 160, H / 2 - 110, 320, 180);
        PlaceIntroFrame(from);

        Tween.Run(180, Tween.OutCubic, k => { foreach (var p in IntroCorners) p.Opacity = k; IntroCount.Opacity = k; });
        After(250, () => Tween.Run(700, Tween.InOutCubic, k =>
        {
            PlaceIntroFrame(new Rect(Tween.Lerp(from.X, to.X, k), Tween.Lerp(from.Y, to.Y, k),
                                     Tween.Lerp(from.Width, to.Width, k), Tween.Lerp(from.Height, to.Height, k)));
            IntroNum.Text = Fmt(27 - 2.5 * k);
        }));
        After(980, () =>
        {
            foreach (var p in IntroCorners) p.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            Tween.Run(400, Tween.OutCubic, k => { IntroMark.Opacity = k; IntroMark.Margin = new Thickness(0, 236 + 6 * (1 - k), 0, 0); });
            Tween.Run(500, Tween.InOutCubic, k => { IntroScan.Opacity = 1 - k; IntroScanT.Y = H * k; });
        });
        After(1500, EndIntro);
    }

    private IEnumerable<Path> IntroCorners => new[] { IntroTL, IntroTR, IntroBL, IntroBR };
    private IEnumerable<FrameworkElement> RevealTargets => new FrameworkElement[] { TitleBar, StatusBar, LeftCol, RightCol };

    private void PlaceIntroFrame(Rect r)
    {
        Canvas.SetLeft(IntroTL, r.Left); Canvas.SetTop(IntroTL, r.Top);
        Canvas.SetLeft(IntroTR, r.Right - 22); Canvas.SetTop(IntroTR, r.Top);
        Canvas.SetLeft(IntroBL, r.Left); Canvas.SetTop(IntroBL, r.Bottom - 22);
        Canvas.SetLeft(IntroBR, r.Right - 22); Canvas.SetTop(IntroBR, r.Bottom - 22);
    }

    private bool _introEnded;
    private void Intro_Skip(object sender, MouseButtonEventArgs e) => EndIntro();

    private void EndIntro()
    {
        if (_introEnded) return;
        _introEnded = true;
        Tween.Run(450, Tween.OutCubic, k =>
        {
            IntroLayer.Opacity = 1 - k;
            IntroScale.ScaleX = IntroScale.ScaleY = 1 + 0.03 * k;
        }, () => IntroLayer.Visibility = Visibility.Collapsed);

        int i = 0;
        foreach (var el in RevealTargets)
        {
            var target = el;
            int delay = i++ * 70;
            After(delay + 1, () => Tween.Run(600, Tween.OutCubic, k =>
            {
                target.Opacity = k;
                if (target.RenderTransform is TranslateTransform t) t.Y = 14 * (1 - k);
            }));
        }
    }

    private void RevealInstant()
    {
        _introEnded = true;
        IntroLayer.Visibility = Visibility.Collapsed;
        foreach (var el in RevealTargets) { el.Opacity = 1; if (el.RenderTransform is TranslateTransform t) t.Y = 0; }
    }

    // ================= 导航 =================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        SetPage(Enum.Parse<Page>(tag));
    }

    private void SetPage(Page p)
    {
        _page = p;
        FrameworkElement show = p switch { Page.Settings => PageSettings, Page.About => PageAbout, _ => PageScale };
        foreach (var el in new FrameworkElement[] { PageScale, PageSettings, PageAbout })
            el.Visibility = el == show ? Visibility.Visible : Visibility.Collapsed;
        (p switch { Page.Settings => NavSettings, Page.About => NavAbout, _ => NavScale }).IsChecked = true;

        if (p == Page.About) { _aboutTween?.Stop(); AboutScroll.ScrollToVerticalOffset(0); _aboutIndex = 0; UpdateAboutParallax(); }

        if (Reduced) { show.Opacity = 1; return; }
        Tween.Run(260, Tween.OutCubic, k =>
        {
            show.Opacity = k;
            if (show.RenderTransform is TranslateTransform t) t.Y = 6 * (1 - k);
        });
    }

    // ================= 模式 =================

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _mute) return;
        SetMode(sender == SegWindow ? Mode.Window : Mode.Screen);
    }

    private void SetMode(Mode m)
    {
        _mode = m;
        bool screen = m == Mode.Screen;
        _mute = true;
        SegScreen.IsChecked = screen;
        SegWindow.IsChecked = !screen;
        _mute = false;

        ScreenPanel.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        GameFrame.Visibility = screen ? Visibility.Visible : Visibility.Hidden; // Hidden 而非 Collapsed：窗口示意图的 VisualBrush 仍需它参与布局
        Desk.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        DimsPanel.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        WmSteps.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        FactsScreen.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        FactsWin.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        PreviewCap.Prefix = screen ? "03" : "04";
        LegendAText.Text = screen ? "游戏画面" : "黑色背景";
        LegendBText.Text = screen ? "黑边" : "桌面";
        PvTip.Text = screen ? "HUD 向中心收拢，扫视更省力" : "窗口去边框后居中，四周可盖黑底";
        if (screen)
        {
            LegendA.Fill = new LinearGradientBrush(Color.FromRgb(0x3A, 0x4D, 0x78), Color.FromRgb(0xC9, 0x82, 0x5D), 90);
            LegendB.Fill = Brushes.Black;
        }
        else
        {
            LegendA.Fill = Brushes.Black;
            LegendB.Fill = new LinearGradientBrush(Color.FromRgb(0x2D, 0x4A, 0x7A), Color.FromRgb(0x5A, 0x3A, 0x6E), 45);
            PlayWinDemo();
        }
        UpdateAppliedUI();
    }

    // ================= 显示器 & 尺寸 =================

    private void ReloadDisplays()
    {
        var keepName = CurrentDisplay?.DeviceName;
        _displays = MonitorService.GetDisplays();
        for (int idx = 0; idx < _displays.Count; idx++)
        {
            var d = _displays[idx];
            string inch = d.DiagonalInches > 0 ? $" · {Math.Round(d.DiagonalInches):0}″" : "";
            d.UiName = $"显示器 {idx + 1}{inch} {d.NativeWidth}×{d.NativeHeight}";
        }
        MonitorCombo.ItemsSource = _displays;
        int i = _displays.FindIndex(d => d.DeviceName == keepName);
        MonitorCombo.SelectedIndex = i >= 0 ? i : Math.Max(0, _displays.FindIndex(d => d.IsPrimary));
    }

    private DisplayInfo? CurrentDisplay => MonitorCombo.SelectedItem as DisplayInfo;

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var d = CurrentDisplay;
        if (d == null) return;
        double max = d.DiagonalInches > 0 ? Math.Floor(d.DiagonalInches * 10) / 10 : 27;
        Ruler.Maximum = max;
        Ruler.Minimum = Math.Max(15, Math.Min(21, max - 2));
        if (Ruler.Value > max) Ruler.Value = max;
        Ruler.InvalidateVisual();
        _resOverride = null;
        BuildResChips(d);
        UpdatePreview();
    }

    private record Target(int W, int H, double Inch, double Scale);

    private Target? CurrentTarget(out string? error)
    {
        error = null;
        var d = CurrentDisplay;
        if (d == null) return null;
        try
        {
            int w, h;
            if (_resOverride is { } r) (w, h) = (r.W, r.H);
            else (w, h) = SizeCalculator.ResolutionFor(d, Ruler.Value);

            // 通用后端（CCD）只能用驱动现成分辨率 → 吸附到最近可用值；窗口模式不吸附
            _snapped = false;
            if (_mode == Mode.Screen && _resOverride == null && _scaler.SnapTarget(d, w, h) is { } s)
            {
                (w, h) = s;
                _snapped = true;
            }

            bool exact = _resOverride == null && !_snapped;
            double inch = exact ? Ruler.Value
                : d.DiagonalInches > 0 ? SizeCalculator.InchesFor(d, w, h) : 0;
            double scale = exact
                ? (d.DiagonalInches > 0 ? Math.Min(1, Ruler.Value / d.DiagonalInches) : 1)
                : (double)w / d.NativeWidth;
            return new Target(w, h, inch, scale);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        var d = CurrentDisplay;
        if (d == null) return;
        SnapHint.Visibility = Visibility.Collapsed;
        // 通用后端候选为空时提前说明，别拿算出来的分辨率去应用
        if (_mode == Mode.Screen && _scaler.AvailableTargets(d) is { Count: 0 })
        {
            SubText.Text = "当前显示器没有更小的可用分辨率，可改用「仅游戏窗口」模式";
            return;
        }
        var t = CurrentTarget(out var err);
        if (t == null) { SubText.Text = err ?? ""; return; }

        if (_snapped && _mode == Mode.Screen)
        {
            SnapHint.Text = $"已吸附到最近的可用分辨率（{t.W}×{t.H}）";
            SnapHint.Visibility = Visibility.Visible;
        }

        bool pro = _resOverride == null && Math.Abs(t.Inch - 24.5) < 0.05;
        string inchText = (_resOverride != null || _snapped ? "≈" : "") + Fmt(t.Inch);
        InchText.Text = inchText;
        GhostNum.Text = Fmt(t.Inch);
        BadgeText.Text = pro ? "PRO STANDARD" : _resOverride != null ? "BY RESOLUTION" : "CUSTOM";
        Badge.SetResourceReference(Border.BackgroundProperty, pro ? "AccentBrush" : "Surface2Brush");
        BadgeText.SetResourceReference(TrackedText.ForegroundProperty, pro ? "AccentInkBrush" : "Text2Brush");

        string native = d.DiagonalInches > 0 ? $"{Math.Round(d.DiagonalInches):0}″" : "原生";
        SubText.Inlines.Clear();
        if (pro)
        {
            SubText.Inlines.Add(new Run("CS2 / VALORANT 职业赛标准尺寸 · "));
            SubText.Inlines.Add(NumRun($"{native} → 24.5″"));
        }
        else
        {
            SubText.Inlines.Add(NumRun($"{native} → {Fmt(t.Inch)}″"));
            SubText.Inlines.Add(new Run($" · 等效 {t.W}×{t.H}"));
        }

        Ruler.SelectedPreset = _resOverride == null ? Ruler.Value : double.NaN;
        RoRes.Text = $"{t.W}×{t.H}";
        RoScale.Inlines.Clear();
        RoScale.Inlines.Add(new Run((t.Scale * 100).ToString("0.0", CultureInfo.InvariantCulture)));
        RoScale.Inlines.Add(SmallRun("%"));
        int bx = (d.NativeWidth - t.W) / 2, by = (d.NativeHeight - t.H) / 2;
        RoBorder.Inlines.Clear();
        RoBorder.Inlines.Add(new Run(bx.ToString()));
        RoBorder.Inlines.Add(SmallRun($" / {by}px"));

        foreach (var tb in ProfilePanel.Children.OfType<ToggleButton>())
            tb.IsChecked = _resOverride == null && tb.Tag is Profile p && Math.Abs(p.Inch - t.Inch) < 0.05;
        foreach (var tb in ResPanel.Children.OfType<ToggleButton>())
            tb.IsChecked = _resOverride is { } ro && tb.Tag is ValueTuple<int, int> v && v.Item1 == ro.W && v.Item2 == ro.H;

        _gameTarget = t.Scale;
        PvSpec.Text = $"{native} · {d.NativeWidth}×{d.NativeHeight} · {AspectText(d.NativeWidth, d.NativeHeight)}";
        DimInnerText.Text = $"{t.W} px";
        DimOuterText.Text = $"{d.NativeWidth} px · 原生";
        WinInch.Text = Fmt(t.Inch) + "″";
        WinSizeText.Text = (_resOverride != null ? "≈" : "") + Fmt(t.Inch);
        WinResText.Text = $"  {t.W}×{t.H}";
        if (_mode == Mode.Window && _winStep >= 2 && !_winDemoRunning) SetWinStep(_winStep, animate: false); // 示意图跟着尺寸走
        UpdateAppliedUI();
    }

    // Run 用资源引用而非取值，切换主题时颜色跟着变
    private Run NumRun(string s) => Themed(new Run(s) { FontFamily = (FontFamily)FindResource("NumFont") }, "TextBrush");
    private Run SmallRun(string s) => Themed(new Run(s) { FontSize = 12 }, "Text3Brush");
    private static Run Themed(Run r, string brushKey) { r.SetResourceReference(TextElement.ForegroundProperty, brushKey); return r; }

    private static string Fmt(double v)
    {
        double r = Math.Round(v * 10) / 10;
        return r.ToString(Math.Abs(r - Math.Round(r)) < 0.001 ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    private static string AspectText(int w, int h)
    {
        int a = w, b = h;
        while (b != 0) (a, b) = (b, a % b);
        return a > 0 ? $"{w / a}:{h / a}" : "";
    }

    private void AnimateInch(double to)
    {
        _resOverride = null;
        _inchTween?.Stop();
        if (Reduced) { Ruler.Value = to; return; }
        double from = Ruler.Value;
        _inchTween = Tween.Run(260, Tween.OutCubic, k => Ruler.Value = Tween.Lerp(from, to, k), () => Ruler.Value = to);
    }

    private void BuildResChips(DisplayInfo d)
    {
        ResPanel.Children.Clear();
        // 通用后端只能选驱动现成分辨率；NV 后端不受限，沿用常用候选
        var candidates = _scaler.AvailableTargets(d) is { } avail
            ? avail
            : new List<(int W, int H)> { (2560,1440),(2400,1350),(2304,1296),(2240,1260),(2048,1152),(1920,1080),(1680,1050),(1600,900) };
        foreach (var (w, h) in candidates)
        {
            if (w >= d.NativeWidth || h >= d.NativeHeight) continue;
            var tb = new ToggleButton { Style = (Style)FindResource("ChipToggle"), Tag = (w, h) };
            var txt = new TextBlock();
            txt.Inlines.Add(new Run($"{w}×{h}"));
            if (d.DiagonalInches > 0)
                txt.Inlines.Add(Themed(new Run($"  ≈{Fmt(SizeCalculator.InchesFor(d, w, h))}″") { FontSize = 10 }, "Text3Brush"));
            tb.Content = txt;
            tb.Click += (_, _) =>
            {
                _inchTween?.Stop();
                _resOverride = _resOverride is { } cur && cur.W == w && cur.H == h ? null : (w, h);
                UpdatePreview();
            };
            ResPanel.Children.Add(tb);
        }
    }

    private void BuildProfiles()
    {
        while (ProfilePanel.Children.Count > 1) ProfilePanel.Children.RemoveAt(1);
        foreach (var p in _settings.Profiles)
        {
            var tb = new ToggleButton { Style = (Style)FindResource("ChipToggle"), Tag = p, ToolTip = "右键删除" };
            var txt = new TextBlock();
            txt.Inlines.Add(new Run(p.Name));
            txt.Inlines.Add(Themed(new Run($"  {Fmt(p.Inch)}″") { FontSize = 10 }, "Text3Brush"));
            tb.Content = txt;
            tb.Click += (_, _) => AnimateInch(p.Inch);
            tb.MouseRightButtonUp += (_, _) =>
            {
                _settings.Profiles.Remove(p);
                SettingsStore.Save(_settings);
                BuildProfiles(); UpdatePreview();
                Toast($"已删除配置：{p.Name}");
            };
            ProfilePanel.Children.Add(tb);
        }
        var add = new Button { Style = (Style)FindResource("ChipAdd"), Content = "＋ 保存", ToolTip = "把当前尺寸存为配置" };
        add.Click += (_, _) => SaveProfile();
        ProfilePanel.Children.Add(add);
    }

    private void SaveProfile()
    {
        var t = CurrentTarget(out _);
        if (t == null) return;
        double inch = Math.Round(t.Inch * 10) / 10;
        if (_settings.Profiles.Any(p => Math.Abs(p.Inch - inch) < 0.05)) { Toast($"{Fmt(inch)}″ 已在我的配置里"); return; }
        if (_settings.Profiles.Count >= 5) { Toast("最多保存 5 个配置，右键可删除"); return; }
        _settings.Profiles.Add(new Profile($"自定义 {_settings.Profiles.Count + 1}", inch));
        SettingsStore.Save(_settings);
        BuildProfiles(); UpdatePreview();
        Toast($"已保存配置：{Fmt(inch)}″ · {t.W}×{t.H}");
    }

    // ================= 状态条 & 主按钮 =================

    private void SetStatus(params (string Text, bool Strong)[] parts)
    {
        StatusMsg.Inlines.Clear();
        foreach (var (text, strong) in parts)
            StatusMsg.Inlines.Add(strong
                ? Themed(new Run(text) { FontFamily = (FontFamily)FindResource("NumFont"), FontSize = 13.5 }, "TextBrush")
                : new Run(text));
    }

    /// <summary>应用失败：状态条转 ERROR（警告色），完整原因常驻，附「查看日志」入口。</summary>
    private void SetStatusError(string msg)
    {
        _statusError = true;
        StatusTagText.Text = "ERROR";
        StatusTag.SetResourceReference(Border.BackgroundProperty, "WarnSoftBrush");
        StatusTagText.SetResourceReference(TrackedText.ForegroundProperty, "WarnBrush");
        StatusTagDot.SetResourceReference(Shape.FillProperty, "WarnBrush");
        StatusBar.SetResourceReference(Border.BorderBrushProperty, "WarnBrush");
        SetDotPulse(false);
        SetStatus(("应用失败：", false), (msg, true));
        StatusLogLink.Visibility = Visibility.Visible;
    }

    private void ClearStatusError()
    {
        _statusError = false;
        StatusLogLink.Visibility = Visibility.Collapsed;
    }

    private void StatusLog_Click(object sender, RoutedEventArgs e)
    {
        SetPage(Page.Settings);
        PageSettings.ScrollToEnd();
        LogBox.ScrollToEnd();
    }

    private void UpdateAppliedUI()
    {
        if (!_ready) return;
        var st = StateStore.Load();
        bool live = st != null;
        var t = CurrentTarget(out _);
        bool same = live && t != null && st!.TargetW == t.W && st.TargetH == t.H;
        var d = CurrentDisplay;

        // 状态条（错误态保持，不被常规刷新覆盖）
        if (!_statusError)
        {
            StatusTagText.Text = live ? "LIVE" : "READY";
            StatusTag.SetResourceReference(Border.BackgroundProperty, live ? "AccentBrush" : "Surface2Brush");
            StatusTagText.SetResourceReference(TrackedText.ForegroundProperty, live ? "AccentInkBrush" : "Text2Brush");
            StatusTagDot.SetResourceReference(Shape.FillProperty, live ? "AccentInkBrush" : "Text3Brush");
            StatusBar.SetResourceReference(Border.BorderBrushProperty, live ? "AccentLineBrush" : "LineBrush");
            SetDotPulse(live);
            if (live)
            {
                string inch = d != null && d.DiagonalInches > 0 ? $"{Fmt(SizeCalculator.InchesFor(d, st!.TargetW, st.TargetH))}″ · " : "";
                SetStatus(("已缩放至 ", false), ($"{inch}{st!.TargetW}×{st.TargetH}", true), ("　游戏内选「桌面分辨率」即可", false));
            }
            else if (_mode == Mode.Window)
                SetStatus(("只缩选中的游戏窗口，其余桌面不受影响", false));
            else if (d != null)
                SetStatus(("原生 ", false), ($"{d.NativeWidth}×{d.NativeHeight}", true), ("　选择目标尺寸后点「应用」", false));
        }

        // 主按钮
        string label = _resOverride is { } r ? $"{r.W}×{r.H}" : t != null ? $"{Fmt(t.Inch)}″" : "";
        MainLabel.Inlines.Clear();
        if (!live)
        {
            MainBtn.Style = (Style)FindResource("PrimaryBtn");
            MainIco.Text = "▶";
            MainLabel.Inlines.Add(new Run("应用 "));
            MainLabel.Inlines.Add(new Run(label) { FontFamily = (FontFamily)FindResource("NumFont"), FontSize = 17 });
            SecBtn.Visibility = Visibility.Collapsed;
        }
        else if (same)
        {
            MainBtn.Style = (Style)FindResource("RestoreBtn");
            MainIco.Text = "↺";
            MainLabel.Inlines.Add(new Run("还原全屏"));
            SecBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            MainBtn.Style = (Style)FindResource("PrimaryBtn");
            MainIco.Text = "▶";
            MainLabel.Inlines.Add(new Run("切换到 "));
            MainLabel.Inlines.Add(new Run(label) { FontFamily = (FontFamily)FindResource("NumFont"), FontSize = 17 });
            SecBtn.Visibility = Visibility.Visible;
        }

        _tray?.Update($"应用 {label}", live, live ? $"AimFrame · 生效中 {st!.TargetW}×{st.TargetH}" : "AimFrame · 待命");
    }

    private Storyboard? _pulse;
    private void SetDotPulse(bool on)
    {
        _pulse?.Stop();
        StatusTagDot.Opacity = 1;
        if (!on || Reduced) return;
        var a = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(a, StatusTagDot);
        Storyboard.SetTargetProperty(a, new PropertyPath(OpacityProperty));
        _pulse = new Storyboard();
        _pulse.Children.Add(a);
        _pulse.Begin();
    }

    // ================= 应用 / 还原 =================

    private void Main_Click(object sender, RoutedEventArgs e)
    {
        var st = StateStore.Load();
        var t = CurrentTarget(out _);
        if (st != null && t != null && st.TargetW == t.W && st.TargetH == t.H) DoRestore();
        else DoApply(confirm: true);
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => DoRestore();

    /// <summary>热键 / 托盘：已生效则还原，否则应用当前选择（不弹确认——窗口可能不在前台）。</summary>
    private void ToggleFromOutside(bool forceApply = false)
    {
        if (_mode != Mode.Screen || !SegScreen.IsEnabled) { Toast("热键只用于「整个屏幕」模式"); return; }
        if (!forceApply && StateStore.Load() != null) DoRestore();
        else DoApply(confirm: false);
    }

    private void DoApply(bool confirm)
    {
        var d = CurrentDisplay;
        var t = CurrentTarget(out var err);
        if (d == null || t == null) { if (err != null) Toast(err); return; }

        var st = StateStore.Load();
        if (st != null && (st.TargetW != t.W || st.TargetH != t.H))
        {
            Log($"切换尺寸：先还原 {st.TargetW}×{st.TargetH}");
            DoRestore(quiet: true);
        }

        ClearStatusError();
#if DEBUG
        // 开发用：模拟应用失败，验证 ERROR 状态条
        if (Environment.GetEnvironmentVariable("SCREENSIZER_FAIL_APPLY") == "1")
        {
            Log("✗ 模拟应用失败（SCREENSIZER_FAIL_APPLY=1）");
            SetStatusError("模拟失败：用于验证 ERROR 状态条与查看日志入口");
            return;
        }
#endif
        Log($"{(_scaler is NvidiaScaler ? "驱动级" : "系统")}缩放 {t.W}×{t.H} → {d.DeviceName}");
        var r = _scaler.Apply(d, t.W, t.H);
        Log(r.Ok ? "✓ " + r.Message : "✗ " + r.Message);
        ReloadDisplays();
        UpdateAppliedUI();
        if (!r.Ok) { SetStatusError(r.Message); return; }

        PlayApplyFx(t.Scale);
        if (confirm && _settings.ConfirmCountdown && IsVisible) After(Reduced ? 50 : 700, () => ShowConfirm(t.W, t.H));
        else Toast($"已缩放至 {t.W}×{t.H}");
    }

    /// <summary>已应用状态记录的后端；无记录时用当前选择的后端。</summary>
    private IScreenScaler ScalerFor(AppliedState? st) =>
        st != null ? (st.Backend == _ccd.Backend ? _ccd : _nvidia) : _scaler;

    private bool DoRestore(bool quiet = false)
    {
        CloseConfirm();
        ClearStatusError();
        bool did = false;

        var st = StateStore.Load();
        if (st != null)
        {
            var sc = ScalerFor(st);
            var r = sc.Restore();
            Log(r.Ok ? $"✓ {sc.Backend}: " + r.Message : $"✗ {sc.Backend}: " + r.Message);
            did = r.Ok;
            if (!r.Ok && CurrentDisplay is { } d)
            {
                var fr = sc.ForceRestore(d);
                Log(fr.Ok ? "✓ 应急: " + fr.Message : "✗ 应急: " + fr.Message);
                did |= fr.Ok;
            }
        }
        else if (_scaler.IsSupported && CurrentDisplay is { } dd && dd.CurrentWidth != dd.NativeWidth)
        {
            var fr = _scaler.ForceRestore(dd);
            Log(fr.Ok ? "✓ " + fr.Message : "✗ " + fr.Message);
            did = fr.Ok;
        }

        if (WindowList.SelectedItem is WindowScaler.GameWindow gw && _windowScaler.HasSaved(gw.Hwnd))
        {
            var r = _windowScaler.Restore(gw.Hwnd);
            Log(r.Ok ? "✓ 窗口: " + r.Message : "✗ 窗口: " + r.Message);
            did = true;
        }
        if (_backdrop != null) { _backdrop.Close(); _backdrop = null; did = true; }

        ReloadDisplays();
        UpdateAppliedUI();
        if (!quiet) Toast(did ? "已还原到原生分辨率" : "当前已是原生状态");
        if (did && !quiet) PlayScan();
        return did;
    }

    private void ForceRestore(object? sender, RoutedEventArgs? e)
    {
        CloseConfirm();
        ClearStatusError();
        var d = CurrentDisplay;
        if (d != null)
        {
            var r = ScalerFor(StateStore.Load()).ForceRestore(d);
            Log(r.Ok ? "✓ 应急还原: " + r.Message : "✗ 应急还原: " + r.Message);
        }
        if (_backdrop != null) { _backdrop.Close(); _backdrop = null; }
        ReloadDisplays();
        UpdateAppliedUI();
        Toast("已强制恢复原生设置");
    }

    // ================= 应用特效：收缩 + 扫描 + 锁定 + 故障 =================

    private void PlayApplyFx(double scale)
    {
        if (Reduced) return;
        _gameAnimating = true;
        Tween.Run(560, Tween.SnapBack, k => { _gameScale = Tween.Lerp(1, scale, k); }, () => { _gameScale = scale; _gameAnimating = false; });
        PlayScan();

        After(380, () => Tween.Run(450, Tween.OutCubic, k =>
        {
            foreach (var p in Corners.Children.OfType<Path>())
                if (p.RenderTransform is ScaleTransform s) { s.ScaleX = s.ScaleY = Tween.Lerp(2.4, 1, k); p.Opacity = Math.Min(1, k * 1.6); }
        }));

        // 数字故障：两层错位色影闪两下
        void Glitch(double ax, double bx, double op)
        {
            GlitchA.Opacity = GlitchB.Opacity = op;
            ((TranslateTransform)GlitchA.RenderTransform).X = ax;
            ((TranslateTransform)GlitchB.RenderTransform).X = bx;
        }
        Glitch(3, -3, 0.85);
        After(110, () => Glitch(-2, 2, 0.7));
        After(220, () => Glitch(0, 0, 0));

        After(300, () =>
        {
            double w = StatusBar.ActualWidth;
            Tween.Run(700, Tween.OutCubic, k => { StatusSweepT.X = Tween.Lerp(-260, w, k); StatusSweep.Opacity = 1; },
                () => StatusSweep.Opacity = 0);
        });
    }

    private void PlayScan()
    {
        if (Reduced) return;
        Tween.Run(600, Tween.InOutCubic, k => { ScanRect.Opacity = 1 - k * 0.3; ScanT.Y = Tween.Lerp(-80, ScreenH, k); },
            () => ScanRect.Opacity = 0);
    }

    // ================= 确认倒计时 =================

    private void ShowConfirm(int w, int h)
    {
        if (StateStore.Load() == null) return;
        ModalRes.Text = $"{w}×{h}";
        _confirmLeft = 15;
        UpdateRing();
        ModalLayer.Visibility = Visibility.Visible;
        MainContent.Effect = Reduced ? null : new BlurEffect { Radius = 6 };
        if (!Reduced) Tween.Run(220, Tween.OutCubic, k =>
        {
            ModalLayer.Opacity = k;
            ModalCard.RenderTransform = new TranslateTransform(0, 8 * (1 - k));
        });
        else ModalLayer.Opacity = 1;

        _confirmTimer?.Stop();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmLeft--;
            UpdateRing();
            if (_confirmLeft <= 0)
            {
                Log("15 秒未确认，自动还原");
                DoRestore(quiet: true);
                Toast("未确认，已自动还原");
            }
        };
        _confirmTimer.Start();
        Activate();
    }

    private void UpdateRing()
    {
        RingNum.Text = _confirmLeft.ToString();
        double frac = Math.Clamp(_confirmLeft / 15.0, 0, 1);
        const double cx = 32, cy = 32, rr = 28;
        if (frac >= 0.999) { RingArc.Data = new EllipseGeometry(new Point(cx, cy), rr, rr); return; }
        if (frac <= 0) { RingArc.Data = null; return; }
        double ang = frac * 2 * Math.PI;
        var start = new Point(cx, cy - rr);
        var end = new Point(cx + rr * Math.Sin(ang), cy - rr * Math.Cos(ang));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(rr, rr), 0, frac > 0.5, SweepDirection.Clockwise, true));
        RingArc.Data = new PathGeometry(new[] { fig });
    }

    private void CloseConfirm()
    {
        _confirmTimer?.Stop();
        if (ModalLayer.Visibility != Visibility.Visible) return;
        ModalLayer.Visibility = Visibility.Collapsed;
        MainContent.Effect = null;
    }

    private void Keep_Click(object sender, RoutedEventArgs e) { CloseConfirm(); Toast("设置已保留 · 玩完记得还原"); }
    private void Revert_Click(object sender, RoutedEventArgs e) { DoRestore(quiet: true); Toast("已撤销本次修改"); }

    // ================= 自定义热键 =================

    private void RefreshHotkeyUI()
    {
        HkToggleKey.Text = _toggleHk.ToString();
        HkForceKey.Text = _forceHk.ToString();
        HkToggleState.Text = _hotkeys?.ToggleOk == true ? "点击右侧按钮重设 · 游戏中无需切出" : "被其他程序占用 · 点击右侧重设";
        HkForceState.Text = _hotkeys?.ForceOk == true ? "任何状态下强制恢复原生" : "被其他程序占用 · 点击右侧重设";
        RenderKbdChips(StatusKbd);
        RenderKbdChips(MainBtnKbd);
    }

    /// <summary>把当前「应用/还原」组合键渲染成键帽；两处（状态条 / 主按钮）样式不同。</summary>
    private void RenderKbdChips(StackPanel panel)
    {
        panel.Children.Clear();
        bool inMainBtn = ReferenceEquals(panel, MainBtnKbd);
        foreach (var part in _toggleHk.ToString().Split('+'))
        {
            var b = new Border { Margin = new Thickness(3, 0, 0, 0) };
            if (inMainBtn)
            {
                b.BorderThickness = new Thickness(1);
                b.CornerRadius = new CornerRadius(3);
                b.Padding = new Thickness(5, 0, 5, 0);
                b.SetBinding(Border.BorderBrushProperty,
                    new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
                b.Child = new TextBlock { Text = part, FontFamily = (FontFamily)FindResource("NumFont"), FontSize = 10.5 };
            }
            else
            {
                b.Style = (Style)FindResource("Kbd");
                b.Child = new TextBlock { Style = (Style)FindResource("KbdText"), Text = part };
            }
            panel.Children.Add(b);
        }
    }

    /// <summary>点「重设」后进入录入态：下一次按下的组合键成为新热键，Esc 取消。</summary>
    private void HkBind_Click(object sender, RoutedEventArgs e)
    {
        _captureId = sender == HkToggleBind ? HotkeyService.IdToggle : HotkeyService.IdForce;
        (_captureId == HotkeyService.IdToggle ? HkToggleKey : HkForceKey).Text = "按新组合键… Esc 取消";
        ((Button)sender).Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureId != 0)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { _captureId = 0; RefreshHotkeyUI(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return; // 等真正的按键
            var hk = HotkeyCombo.FromKeys(Keyboard.Modifiers, key);
            if (hk.Mods == 0) { Toast("需要至少一个修饰键（Ctrl / Alt / Shift / Win）"); return; }
            var other = _captureId == HotkeyService.IdToggle ? _forceHk : _toggleHk;
            if (hk == other) { Toast("两个热键不能用同一个组合键"); return; }
            if (_hotkeys != null && _hotkeys.Rebind(_captureId, hk))
            {
                if (_captureId == HotkeyService.IdToggle) { _toggleHk = hk; _settings.HotkeyToggle = hk.ToString(); }
                else { _forceHk = hk; _settings.HotkeyForce = hk.ToString(); }
                SettingsStore.Save(_settings);
                Toast($"热键已改为 {hk}");
            }
            else Toast($"{hk} 被其他程序占用，换一个试试");
            _captureId = 0;
            RefreshHotkeyUI();
            return;
        }
        if (ModalLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter) { Keep_Click(this, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { Revert_Click(this, e); e.Handled = true; }
            return;
        }
        if (_page == Page.Scale && e.Key is Key.Left or Key.Right && Keyboard.FocusedElement is not TextBox)
        {
            _resOverride = null;
            Ruler.Value = Math.Clamp(Math.Round((Ruler.Value + (e.Key == Key.Right ? 0.1 : -0.1)) * 10) / 10, Ruler.Minimum, Ruler.Maximum);
            e.Handled = true;
        }
    }

    // ================= 仅游戏窗口 =================

    private void RefreshWindows(object? sender, RoutedEventArgs? e)
    {
        WindowList.ItemsSource = WindowScaler.ListWindows((uint)Environment.ProcessId);
        if (sender != null) Toast($"已刷新窗口列表 · {WindowList.Items.Count} 个");
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is WindowScaler.GameWindow gw)
        {
            GTitle.Text = gw.Title;
            GIconText.Text = gw.Initials;
            PlayWinDemo();
        }
    }

    private void WinApply_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowScaler.GameWindow gw) { Toast("先在列表里选一个游戏窗口"); return; }
        var d = CurrentDisplay;
        if (d == null) { Toast("先选一个显示器"); return; }
        var t = CurrentTarget(out var err);
        if (t == null) { Toast(err ?? "无法计算目标尺寸"); return; }
        // 当前选中显示器的工作区（物理像素），不是主屏
        var dm = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
        NativeMethods.EnumDisplaySettingsEx(d.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0);
        var hmon = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = dm.dmPositionX + (int)dm.dmPelsWidth / 2, Y = dm.dmPositionY + (int)dm.dmPelsHeight / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(hmon, ref mi);
        if (BackdropBox.IsChecked == true) ShowBackdrop(mi.rcMonitor);
        var r = _windowScaler.Apply(gw.Hwnd, t.W, t.H, mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height, TopmostBox.IsChecked == true);
        Log(r.Ok ? $"✓ {gw.Title}: {r.Message}" : "✗ " + r.Message);
        Toast(r.Ok ? $"已缩放 {gw.Title}" : r.Message);
        PlayWinDemo();
    }

    /// <summary>整屏黑底，盖住所选显示器（含任务栏区域；任务栏本身仍在最前）。</summary>
    private void ShowBackdrop(NativeMethods.RECT monitor)
    {
        if (_backdrop != null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _backdrop = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = false, Background = Brushes.Black, Topmost = true,
            ShowInTaskbar = false,
            Left = monitor.Left / dpi.DpiScaleX, Top = monitor.Top / dpi.DpiScaleY,
            Width = monitor.Width / dpi.DpiScaleX, Height = monitor.Height / dpi.DpiScaleY,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false,
        };
        _backdrop.Show();
    }

    private void WinOpt_Click(object sender, RoutedEventArgs e) => SetWinStep(_winStep, animate: true);
    private void WinDemo_Click(object sender, RoutedEventArgs e) => PlayWinDemo();
    private void Step_Click(object sender, MouseButtonEventArgs e)
    {
        StopWinTimers();
        if (sender is FrameworkElement { Tag: string s }) SetWinStep(int.Parse(s), animate: true);
    }

    private bool _winDemoRunning;
    private void StopWinTimers() { foreach (var t in _winTimers) t.Stop(); _winTimers.Clear(); _winDemoRunning = false; }

    /// <summary>示意动画：原窗口 → 去边框 → 缩放居中 → 黑底 / 置顶。</summary>
    private void PlayWinDemo()
    {
        StopWinTimers();
        if (Reduced) { SetWinStep(3, animate: false); return; }
        SetWinStep(0, animate: false);
        _winDemoRunning = true;
        foreach (var (s, ms) in new[] { (1, 700), (2, 1300), (3, 2100) })
            _winTimers.Add(After(ms, () => { SetWinStep(s, animate: true); if (s == 3) _winDemoRunning = false; }));
    }

    private Rect WinRect(int step)
    {
        if (step < 2) return new Rect(ScreenW * 0.09, ScreenH * 0.09, ScreenW * 0.6, ScreenH * 0.64);
        double s = _gameTarget;
        return new Rect(ScreenW * (1 - s) / 2, ScreenH * (1 - s) / 2, ScreenW * s, ScreenH * s);
    }

    private void SetWinStep(int step, bool animate)
    {
        _winStep = step;
        foreach (var (b, i) in new[] { (Step0, 0), (Step1, 1), (Step2, 2), (Step3, 3) })
        {
            bool on = i == step;
            if (on) b.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            else b.Background = Brushes.Transparent;
            if (b.Child is TextBlock tb)
            {
                tb.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "Text3Brush");
                tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        var from = new Rect(Canvas.GetLeft(GWin), Canvas.GetTop(GWin), GWin.Width, GWin.Height);
        var to = WinRect(step);
        double barFrom = GBar.Height, barTo = step >= 1 ? 0 : 14;
        double cornersTo = step >= 2 ? 1 : 0;
        double backTo = step >= 3 && BackdropBox.IsChecked == true ? 1 : 0;
        double pinTo = step >= 3 && TopmostBox.IsChecked == true ? 1 : 0;
        double c0 = GCorners.Opacity, b0 = DeskBackdrop.Opacity, p0 = GPin.Opacity;
        GWinBody.CornerRadius = new CornerRadius(step >= 1 ? 0 : 4);

        void Frame(double k)
        {
            Canvas.SetLeft(GWin, Tween.Lerp(from.X, to.X, k));
            Canvas.SetTop(GWin, Tween.Lerp(from.Y, to.Y, k));
            GWin.Width = Math.Max(1, Tween.Lerp(from.Width, to.Width, k));
            GWin.Height = Math.Max(1, Tween.Lerp(from.Height, to.Height, k));
            GBar.Height = Tween.Lerp(barFrom, barTo, k);
            GCorners.Opacity = Tween.Lerp(c0, cornersTo, k);
            DeskBackdrop.Opacity = Tween.Lerp(b0, backTo, k);
            Taskbar.Opacity = 1 - DeskBackdrop.Opacity;
            GPin.Opacity = Tween.Lerp(p0, pinTo, k);
        }
        if (animate && !Reduced) Tween.Run(step == 2 ? 600 : 380, Tween.InOutCubic, Frame);
        else Frame(1);
    }

    // ================= 设置 =================

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        for (int i = 0; i < ThemeService.Accents.Length; i++)
        {
            var a = ThemeService.Accents[i];
            var r = new RadioButton { Style = (Style)FindResource("SwatchRadio"), Background = new SolidColorBrush(a.Color), Tag = i, ToolTip = a.Name };
            r.Checked += (s, _) =>
            {
                if (_mute) return;
                _settings.Accent = (int)((RadioButton)s).Tag;
                SettingsStore.Save(_settings);
                ThemeService.Apply(_settings.Theme, _settings.Accent);
            };
            SwatchPanel.Children.Add(r);
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_mute || sender is not RadioButton { Tag: string mode }) return;
        _settings.Theme = mode;
        SettingsStore.Save(_settings);
        ThemeService.Apply(_settings.Theme, _settings.Accent);
    }

    private void Reduce_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReduceMotion = ReduceBox.IsChecked == true;
        SettingsStore.Save(_settings);
        StartGlow();
        UpdateAppliedUI();
        foreach (var l in new FrameworkElement[] { LySky, LyMtn, LyBld, LyGnd })
            if (l.RenderTransform is TranslateTransform t) t.X = t.Y = 0;
        UpdateAboutParallax();
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try { StartupService.SetAutoStart(AutoStartBox.IsChecked == true); }
        catch (Exception ex) { Log("✗ 开机自启设置失败: " + ex.Message); }
    }

    private void Behavior_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinimizeToTray = TrayBox.IsChecked == true;
        _settings.ConfirmCountdown = ConfirmBox.IsChecked == true;
        _settings.DriftDetect = DriftDetectBox.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    // ================= 关于页：整屏滚动 + 视差 =================

    private void About_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double h = AboutScroll.ActualHeight;
        foreach (var s in new[] { S1, S2, S3, S4 }) s.Height = h;
        if (_parallax.Count == 0)
        {
            void Reg(FrameworkElement el, int sec, double sy, double sx = 0, double x0 = 0)
            {
                el.RenderTransform = new TranslateTransform();
                _parallax.Add((el, sec, sy, sx, x0));
            }
            Reg(S1Huge, 0, 0.45); Reg(S1Copy, 0, -0.15);
            Reg(S2W1, 1, 0, -0.7, -120); Reg(S2W2, 1, 0, 0.55, -320); Reg(S2W3, 1, 0, -0.4, -60); Reg(S2Card, 1, -0.2);
            Reg(S3Left, 2, -0.25); Reg(S3P1, 2, 0.3); Reg(S3P2, 2, -0.1);
            Reg(S4Left, 3, -0.12); Reg(S4Right, 3, 0.08);
        }
        AboutScroll.ScrollToVerticalOffset(_aboutIndex * h);
        UpdateAboutParallax();
    }

    private void About_Scrolled(object sender, ScrollChangedEventArgs e) => UpdateAboutParallax();

    private void UpdateAboutParallax()
    {
        double h = AboutScroll.ActualHeight, off = AboutScroll.VerticalOffset;
        foreach (var (el, sec, sy, sx, x0) in _parallax)
        {
            if (el.RenderTransform is not TranslateTransform t) continue;
            double rel = sec * h - off;
            t.Y = Reduced ? 0 : rel * sy;
            t.X = Reduced ? x0 : x0 + rel * sx;
        }
        int idx = h > 0 ? (int)Math.Round(off / h) : 0;
        int i = 0;
        foreach (var b in AboutDots.Children.OfType<Button>()) b.Tag = i++ == idx ? "on" : null;
    }

    private void About_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_aboutTween != null && _aboutAnimating) return;
        ScrollAboutTo(_aboutIndex + (e.Delta < 0 ? 1 : -1));
    }

    private bool _aboutAnimating;

    private void ScrollAboutTo(int idx)
    {
        idx = Math.Clamp(idx, 0, 3);
        _aboutIndex = idx;
        double h = AboutScroll.ActualHeight, from = AboutScroll.VerticalOffset, to = idx * h;
        _aboutTween?.Stop();
        if (Reduced) { AboutScroll.ScrollToVerticalOffset(to); return; }
        _aboutAnimating = true;
        _aboutTween = Tween.Run(650, Tween.InOutCubic, k => AboutScroll.ScrollToVerticalOffset(Tween.Lerp(from, to, k)),
            () => _aboutAnimating = false);
    }

    private void AboutDot_Click(object sender, RoutedEventArgs e)
        => ScrollAboutTo(AboutDots.Children.IndexOf((UIElement)sender));

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ================= 更新 =================

    private UpdateService.Result? _update;

    private async Task AutoCheckUpdate()
    {
        if (!_settings.AutoUpdate) return;
        if (DateTime.Now - _settings.LastUpdateCheck < TimeSpan.FromHours(24))
        {
            // 24 小时内查过：沿用上次结果，不重复请求
            if (_settings.LatestTag is { } tag && tag != _settings.SkippedVersion && IsNewer(tag))
                ShowUpdate(new UpdateService.Result(UpdateService.Status.Available, tag, $"{UpdateService.RepoUrl}/releases/tag/{tag}"));
            else
            {
                UpdLatestSub.Text = $"上次检查：{_settings.LastUpdateCheck:MM-dd HH:mm}";
                SetUpdState(UpdLatest);
            }
            return;
        }
        await CheckUpdate(manual: false);
    }

    private static bool IsNewer(string tag)
        => Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var v) && new Version(v.Major, v.Minor, Math.Max(0, v.Build)) > UpdateService.Current;

    private async Task CheckUpdate(bool manual)
    {
        SetUpdState(UpdChecking);
        var spin = Tween.Run(100000, Tween.Linear, k => SpinnerRot.Angle = k * 100000 * 0.36);
        var res = await UpdateService.CheckAsync();
        if (manual) await Task.Delay(400); // 让「检查中」至少可见一下
        spin.Stop();

        if (res.Status != UpdateService.Status.Error)
        {
            // 失败不记时间：下次启动会重试
            _settings.LastUpdateCheck = DateTime.Now;
            _settings.LatestTag = res.Tag;
            SettingsStore.Save(_settings);
        }
        else Log("更新检查失败：" + res.Error);

        switch (res.Status)
        {
            case UpdateService.Status.Available when manual || res.Tag != _settings.SkippedVersion:
                ShowUpdate(res);
                Log($"发现新版本 {res.Tag}（GitHub Releases）");
                break;
            case UpdateService.Status.Error:
                SetUpdState(manual ? UpdError : UpdIdle);
                break;
            default:
                UpdLatestSub.Text = $"上次检查：{DateTime.Now:HH:mm}";
                SetUpdState(UpdLatest);
                UpdateDot.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ShowUpdate(UpdateService.Result res)
    {
        _update = res;
        UpdNewVer.Text = res.Tag ?? "";
        UpdDate.Text = res.Date is { } dt ? $"  {dt:yyyy-MM-dd}" : "";
        UpdNotes.Children.Clear();
        foreach (var n in res.Notes ?? Array.Empty<string>())
        {
            var tb = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            tb.Inlines.Add(Themed(new Run("+ ") { FontFamily = (FontFamily)FindResource("NumFont") }, "AccentBrush"));
            tb.Inlines.Add(new Run(n));
            UpdNotes.Children.Add(tb);
        }
        UpdCard.SetResourceReference(Border.BorderBrushProperty, "AccentLineBrush");
        SetUpdState(UpdAvailable);
        UpdateDot.Visibility = Visibility.Visible;
    }

    private void SetUpdState(FrameworkElement show)
    {
        foreach (var el in new FrameworkElement[] { UpdIdle, UpdChecking, UpdAvailable, UpdLatest, UpdError })
            el.Visibility = el == show ? Visibility.Visible : Visibility.Collapsed;
        if (show != UpdAvailable) UpdCard.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = CheckUpdate(manual: true);

    private void UpdDownload_Click(object sender, RoutedEventArgs e) => OpenUrl(_update?.Url ?? UpdateService.ReleasesUrl);

    private void UpdSkip_Click(object sender, RoutedEventArgs e)
    {
        _settings.SkippedVersion = _update?.Tag;
        SettingsStore.Save(_settings);
        UpdLatestSub.Text = $"已忽略 {_update?.Tag}";
        SetUpdState(UpdLatest);
        UpdateDot.Visibility = Visibility.Collapsed;
        Toast($"已忽略 {_update?.Tag}");
    }

    // ================= 工具 =================

    private DispatcherTimer After(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
        return t;
    }

    private void Toast(string msg)
    {
        ToastText.Text = msg;
        _toastTween?.Stop();
        _toastTimer?.Stop();
        _toastTween = Tween.Run(Reduced ? 1 : 250, Tween.OutCubic, k => { ToastBox.Opacity = k; ToastT.Y = 6 * (1 - k); });
        _toastTimer = After(2200, () => _toastTween = Tween.Run(Reduced ? 1 : 250, Tween.OutCubic, k => ToastBox.Opacity = 1 - k));
    }


    private static string LogFile => AppPaths.File("app.log");

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {msg}\n";
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFile)!);
            System.IO.File.AppendAllText(LogFile, line);
        }
        catch { }
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        if (msg.StartsWith("✗") && _ready) Toast("出错了，详情见「设置 › 日志」");
    }
}
