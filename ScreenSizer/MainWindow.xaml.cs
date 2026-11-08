using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenSizer.Models;
using ScreenSizer.Native;
using ScreenSizer.Services;

namespace ScreenSizer;

public partial class MainWindow : Window
{
    private enum Mode { Screen, Window }
    private enum Page { Scale, Guide, Settings, About }

    private List<DisplayInfo> _displays = new();
    private readonly NvidiaScaler _nvidia;
    private readonly WindowScaler _windowScaler = new();
    private GpuService.Gpu _gpu = new(GpuVendor.Unknown, "未知");
    private Mode _mode = Mode.Screen;
    private Window? _backdrop;
    private DispatcherTimer? _displayChangeTimer;
    private (int W, int H)? _resOverride;
    private AppSettings _settings = new();

    private static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));

    /// <summary>署名 —— 改这里。</summary>
    public const string Author = "智宇丶817";

    public MainWindow()
    {
        InitializeComponent();
        _nvidia = new NvidiaScaler(Log);
        Loaded += OnLoaded;
        StateChanged += (s, e) =>
        {
            bool max = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(max ? 0 : 8);
            // 无边框窗口最大化会盖住任务栏，限制到工作区高度
            MaxHeight = max ? SystemParameters.WorkArea.Height : double.PositiveInfinity;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ApplyChrome(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle())?.AddHook(WndProc);
    }

    // ================= 自定义标题栏 =================

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= 分辨率变化监听（游戏调分辨率不干扰） =================

    private const int WM_DISPLAYCHANGE = 0x007E;

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
        return IntPtr.Zero;
    }

    private void DisplayChangeSettled(object? sender, EventArgs e)
    {
        _displayChangeTimer?.Stop();
        ReloadDisplays();

        // 如果窗口被分辨率变化挤到屏幕外，拉回正中
        var wa = SystemParameters.WorkArea;
        if (Left + Width > wa.Right + 40 || Left < wa.Left - Width + 40 ||
            Top + Height > wa.Bottom + 40 || Top < wa.Top - 40)
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        // 应用状态中分辨率被外部改动 → 提示（可在设置里关闭）
        var st = StateStore.Load();
        var d = CurrentDisplay;
        if (_settings.DriftDetect && st != null && d != null &&
            (d.CurrentWidth != st.TargetW || d.CurrentHeight != st.TargetH))
            StatusText.Text = $"检测到分辨率被外部改为 {d.CurrentWidth}×{d.CurrentHeight}（游戏/系统）— 可点「还原全屏」恢复";
        UpdateAppliedUI();
    }

    // ================= 初始化 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsStore.Load();
        AutoStartBox.IsChecked = StartupService.IsAutoStartEnabled();
        DriftDetectBox.IsChecked = _settings.DriftDetect;
        AuthorText.Text = Author;

        _gpu = GpuService.PrimaryGpu();

        bool nv = _gpu.Vendor == GpuVendor.Nvidia && NvApi.IsAvailable;
        string shortGpu = _gpu.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
        BackendChipText.Text = _gpu.Vendor switch
        {
            GpuVendor.Nvidia => nv ? $"驱动级可用 · {shortGpu}" : "NVAPI 不可用",
            GpuVendor.Amd => $"仅窗口模式 · {shortGpu}",
            GpuVendor.Intel => $"仅窗口模式 · {shortGpu}",
            _ => "仅窗口模式",
        };
        if (!nv)
        {
            BackendChip.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xEC));
            BackendChipText.Foreground = Muted;
            SegScreen.IsEnabled = false;
        }

        ReloadDisplays();
        RefreshWindows(null, null);
        SetMode(nv ? Mode.Screen : Mode.Window);
        SetPage(Page.Scale);

        // 27 寸及以上 → 默认推荐职业标准 24.5"
        var d0 = CurrentDisplay;
        if (d0 != null && d0.DiagonalInches >= 26.5)
            InchSlider.Value = 24.5;

        var st = StateStore.Load();
        StatusText.Text = st != null
            ? $"检测到已应用状态：{st.TargetW}×{st.TargetH} — 完成后点「还原全屏」"
            : "拖动滑块或点预设，然后点「应用」";
        UpdateAppliedUI();
    }

    // ================= 导航 =================

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
            SetPage(Enum.Parse<Page>(tag));
    }

    private void SetPage(Page p)
    {
        PageScale.Visibility = p == Page.Scale ? Visibility.Visible : Visibility.Collapsed;
        PageGuide.Visibility = p == Page.Guide ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = p == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = p == Page.About ? Visibility.Visible : Visibility.Collapsed;

        NavScale.Style = (Style)FindResource(p == Page.Scale ? "NavBtnOn" : "NavBtn");
        NavGuide.Style = (Style)FindResource(p == Page.Guide ? "NavBtnOn" : "NavBtn");
        NavSettings.Style = (Style)FindResource(p == Page.Settings ? "NavBtnOn" : "NavBtn");
        NavAbout.Style = (Style)FindResource(p == Page.About ? "NavBtnOn" : "NavBtn");
    }

    // ================= 模式 =================

    private void SegScreen_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Screen);
    private void SegWindow_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Window);

    private void SetMode(Mode m)
    {
        _mode = m;
        bool screen = m == Mode.Screen;
        SegScreen.Style = (Style)FindResource(screen ? "SegBtnOn" : "SegBtn");
        SegScreen.Foreground = screen ? Brushes.Black : Secondary;
        SegScreen.FontWeight = screen ? FontWeights.SemiBold : FontWeights.Normal;
        SegWindow.Style = (Style)FindResource(screen ? "SegBtn" : "SegBtnOn");
        SegWindow.Foreground = screen ? Secondary : Brushes.Black;
        SegWindow.FontWeight = screen ? FontWeights.Normal : FontWeights.SemiBold;

        ScreenPanel.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        UpdateApplyButton();
        StatusText.Text = screen
            ? "整个屏幕缩小为居中画面，对所有程序生效（打 FPS 推荐）"
            : "只缩选中的游戏窗口，其余桌面区域不受影响";
        FooterRight.Text = screen ? "驱动级" : "窗口化";
    }

    // ================= 显示器 & 尺寸 =================

    private void ReloadDisplays()
    {
        var keepName = CurrentDisplay?.DeviceName;
        _displays = MonitorService.GetDisplays();
        for (int idx = 0; idx < _displays.Count; idx++)
        {
            var d = _displays[idx];
            var mon = !string.IsNullOrWhiteSpace(d.MonitorName) &&
                      !d.MonitorName.Contains("Generic", StringComparison.OrdinalIgnoreCase)
                ? $" · {d.MonitorName}" : "";
            d.UiName = $"显示器 {idx + 1}{mon} ({d.NativeWidth}×{d.NativeHeight})";
        }
        MonitorCombo.ItemsSource = _displays;
        int i = _displays.FindIndex(d => d.DeviceName == keepName);
        MonitorCombo.SelectedIndex = i >= 0 ? i : Math.Max(0, _displays.FindIndex(d => d.IsPrimary));
    }

    private DisplayInfo? CurrentDisplay => MonitorCombo.SelectedItem as DisplayInfo;

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var d = CurrentDisplay;
        if (d != null)
        {
            InchSlider.Maximum = d.DiagonalInches > 0 ? Math.Floor(d.DiagonalInches * 10) / 10 : 27;
            if (d.NativeWidth > 0 && d.NativeHeight > 0)
                PreviewPanel.Height = Math.Min(200, PreviewPanel.Width * d.NativeHeight / d.NativeWidth);
            FillResCombo(d);
        }
        UpdatePreview();
    }

    /// <summary>分辨率直选：推荐项（滑块计算结果）+ 常见目标 ≤ 原生。</summary>
    private bool _resMute;

    private void FillResCombo(DisplayInfo d)
    {
        _resMute = true;
        int keep = ResCombo.SelectedIndex;
        var (rw, rh) = SizeCalculator.ResolutionFor(d, InchSlider.Value);
        var items = new List<string> { $"{rw} × {rh}  （推荐）" };
        var candidates = new (int W, int H)[]
        {
            (2560,1440),(2304,1296),(2208,1242),(2048,1152),(1920,1080),(1680,1050),(1600,900),(1280,960),
        };
        foreach (var (w, h) in candidates)
            if (w < d.NativeWidth && h < d.NativeHeight && !(w == rw && h == rh))
                items.Add($"{w} × {h}   ≈{SizeCalculator.InchesFor(d, w, h):0.#}\"");
        ResCombo.ItemsSource = items;
        ResCombo.SelectedIndex = keep > 0 && keep < items.Count ? keep : 0;
        _resMute = false;
    }

    private void ResCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_resMute) return;
        if (ResCombo.SelectedIndex <= 0) { _resOverride = null; UpdatePreview(); return; }
        var s = ResCombo.SelectedItem?.ToString() ?? "";
        var parts = s.Split('×');
        if (parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out int w) &&
            int.TryParse(parts[1].Split('≈')[0].Trim(), out int h))
        {
            _resOverride = (w, h);
            UpdatePreview();
        }
    }

    private void InchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _resOverride = null;
        if (ResCombo != null && ResCombo.SelectedIndex > 0)
        {
            ResCombo.SelectionChanged -= ResCombo_Changed;
            ResCombo.SelectedIndex = 0;
            ResCombo.SelectionChanged += ResCombo_Changed;
        }
        UpdatePreview();
    }

    private void QuickInch(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && double.TryParse(b.Tag?.ToString(), out var v))
            InchSlider.Value = v;
    }

    private void UpdateChipSelection()
    {
        double v = InchSlider.Value;
        foreach (var child in ChipPanel.Children)
            if (child is Button b && double.TryParse(b.Tag?.ToString(), out var t))
            {
                bool sel = Math.Abs(t - v) < 0.05;
                b.Style = (Style)FindResource(sel ? "ChipBtnPro" : "ChipBtn");
                b.Content = sel ? $"✓ {t}″" : (Math.Abs(t - 24.5) < 0.01 ? $"★ {t}″" : $"{t}″");
            }
    }

    private void UpdatePreview()
    {
        if (!IsInitialized) return;
        var d = CurrentDisplay;
        if (d == null) return;

        try
        {
            int w, h; double inch;
            if (_resOverride is { } r)
            {
                (w, h) = r;
                inch = d.DiagonalInches > 0 ? SizeCalculator.InchesFor(d, w, h) : 0;
                InchText.Text = $"≈{inch:0.#}";
            }
            else
            {
                inch = InchSlider.Value;
                (w, h) = SizeCalculator.ResolutionFor(d, inch);
                InchText.Text = inch.ToString("0.#");
            }

            double scale = d.DiagonalInches > 0 ? inch / d.DiagonalInches : 1;
            PreviewImage.Width = PreviewPanel.Width * scale;
            PreviewImage.Height = PreviewPanel.Height * scale;

            bool isPro = Math.Abs(inch - 24.5) < 0.05;
            InchSubText.Text = isPro
                ? "CS / Valorant 职业标准，FPS 最佳选择"
                : $"{d.DiagonalInches:0.#}″ 缩至 {inch:0.#}″";
            PreviewText.Text = $"画面 {w}×{h} · 缩放 {scale:P0} · 每侧黑边约 {(1 - scale) / 2 * d.NativeWidth:0}px";
            InfoResText.Text = $"{w} × {h}";
            InfoInchText.Text = $"≈ {inch:0.#}″ · {scale:P0}";
            if (_resOverride == null) FillResCombo(d); // 刷新「推荐」项的分辨率
            UpdateApplyButton();
            UpdateChipSelection();
        }
        catch (Exception ex) { PreviewText.Text = ex.Message; }
    }

    private void UpdateApplyButton()
    {
        if (_resOverride is { } r) ApplyBtn.Content = $"▶  应用 {r.W}×{r.H}";
        else ApplyBtn.Content = _mode == Mode.Screen
            ? $"▶  应用 {InchSlider.Value:0.#}″"
            : $"▶  应用 {InchSlider.Value:0.#}″ 到窗口";
    }

    /// <summary>已应用状态：绿色横幅 + 还原升为主按钮。</summary>
    private void UpdateAppliedUI()
    {
        var st = StateStore.Load();
        AppliedBanner.Visibility = st != null ? Visibility.Visible : Visibility.Collapsed;
        if (st != null)
        {
            var d = CurrentDisplay;
            string inch = d != null && d.DiagonalInches > 0
                ? $"≈{SizeCalculator.InchesFor(d, st.TargetW, st.TargetH):0.#}″ · " : "";
            AppliedText.Text = $"✓  缩放生效中 · {inch}{st.TargetW}×{st.TargetH} — 游戏内选「桌面分辨率」或 {st.TargetW}×{st.TargetH}";
            ApplyBtn.IsEnabled = false;
            ApplyBtn.Content = $"{st.TargetW}×{st.TargetH} 生效中";
            RestoreBtn.Style = (Style)FindResource("PrimaryBtn");
        }
        else
        {
            ApplyBtn.IsEnabled = true;
            RestoreBtn.Style = (Style)FindResource("GhostBtn");
            UpdateApplyButton();
        }
    }

    // ================= 应用 / 还原 =================

    private (int W, int H)? TargetRes()
    {
        var d = CurrentDisplay;
        if (d == null) return null;
        if (_resOverride is { } r) return r;
        try { return SizeCalculator.ResolutionFor(d, InchSlider.Value); }
        catch (Exception ex) { Log("✗ " + ex.Message); StatusText.Text = ex.Message; return null; }
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        var d = CurrentDisplay;
        var t = TargetRes();
        if (d == null || t == null) return;
        var (w, h) = t.Value;

        if (_mode == Mode.Screen)
        {
            Log($"驱动级缩放 {w}×{h} → {d.DeviceName}");
            var r = _nvidia.Apply(d, w, h);
            Log(r.Ok ? "✓ " + r.Message : "✗ " + r.Message);
            StatusText.Text = r.Ok
                ? $"已应用 {w}×{h} 居中 — 游戏里分辨率选「桌面分辨率」或 {w}×{h}"
                : "应用失败：" + r.Message;
            GuideResText.Text = $"当前生成：{w}×{h}（游戏里也可直接选这个分辨率）";
            UpdateAppliedUI();
        }
        else
        {
            if (WindowList.SelectedItem is not WindowScaler.GameWindow gw) { StatusText.Text = "先在上面列表里选一个游戏窗口"; return; }
            var dpi = VisualTreeHelper.GetDpi(this);
            int sw = (int)(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
            int sh = (int)(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
            if (BackdropBox.IsChecked == true) ShowBackdrop(sw, sh);
            var r = _windowScaler.Apply(gw.Hwnd, w, h, sw, sh, TopmostBox.IsChecked == true);
            Log(r.Ok ? $"✓ {gw.Title}: {r.Message}" : "✗ " + r.Message);
            StatusText.Text = r.Message;
        }
        ReloadDisplays();
    }

    private void Restore(object sender, RoutedEventArgs e)
    {
        bool did = false;

        if (StateStore.Load() != null)
        {
            var r = _nvidia.Restore();
            Log(r.Ok ? "✓ 驱动级: " + r.Message : "✗ 驱动级: " + r.Message);
            did = r.Ok;
            if (!r.Ok) // 常规还原失败 → 应急兜底，保证"还原"永远有响应
            {
                var d = CurrentDisplay;
                if (d != null)
                {
                    var fr = _nvidia.ForceRestore(d);
                    Log(fr.Ok ? "✓ 应急: " + fr.Message : "✗ 应急: " + fr.Message);
                    did |= fr.Ok;
                }
            }
        }
        else if (NvApi.IsAvailable && CurrentDisplay is { } dd)
        {
            // 没有保存状态也要给反馈：强制回原生
            var fr = _nvidia.ForceRestore(dd);
            Log(fr.Ok ? "✓ " + fr.Message : "✗ " + fr.Message);
            did = fr.Ok;
        }

        if (WindowList.SelectedItem is WindowScaler.GameWindow gw && _windowScaler.HasSaved(gw.Hwnd))
        {
            var r = _windowScaler.Restore(gw.Hwnd);
            Log(r.Ok ? "✓ 窗口: " + r.Message : "✗ 窗口: " + r.Message);
            did = true;
        }
        if (_backdrop != null) { _backdrop.Close(); _backdrop = null; }

        StatusText.Text = did ? "已还原到原生状态" : "当前已是原生状态，无需还原";
        ReloadDisplays();
        UpdateAppliedUI();
    }

    // ================= 窗口化 =================

    private void RefreshWindows(object? sender, RoutedEventArgs? e)
    {
        WindowList.ItemsSource = WindowScaler.ListWindows((uint)Environment.ProcessId);
    }

    private void ShowBackdrop(int sw, int sh)
    {
        if (_backdrop != null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            Left = 0, Top = 0,
            Width = sw / dpi.DpiScaleX,
            Height = sh / dpi.DpiScaleY,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
        };
        _backdrop.Show();
    }

    // ================= 设置 =================

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        try { StartupService.SetAutoStart(AutoStartBox.IsChecked == true); }
        catch (Exception ex) { Log("✗ 开机自启设置失败: " + ex.Message); }
    }

    private void DriftDetect_Changed(object sender, RoutedEventArgs e)
    {
        _settings.DriftDetect = DriftDetectBox.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = "已是最新版本 v1.0.0";

    private void Homepage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink h && h.NavigateUri != null)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(h.NavigateUri.ToString()) { UseShellExecute = true }); } catch { }
    }

    private void ForceRestore(object sender, RoutedEventArgs e)
    {
        var d = CurrentDisplay;
        if (d == null) return;
        var r = _nvidia.ForceRestore(d);
        Log(r.Ok ? "✓ 应急还原: " + r.Message : "✗ 应急还原: " + r.Message);
        StatusText.Text = r.Message;
        if (_backdrop != null) { _backdrop.Close(); _backdrop = null; }
        ReloadDisplays();
        UpdateAppliedUI();
    }

    private static readonly string LogFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenSizer", "app.log");

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {msg}\n";
        try { System.IO.File.AppendAllText(LogFile, line); } catch { }
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        if (msg.StartsWith("✗"))
            SetPage(Page.Settings); // 出错自动跳到日志页
    }

    protected override void OnClosed(EventArgs e)
    {
        _backdrop?.Close();
        base.OnClosed(e);
    }
}
