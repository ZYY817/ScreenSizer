using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenSizer.Controls;

/// <summary>
/// 刻度尺选择器：0.1″ 小刻度 / 0.5″ 中刻度 / 1″ 大刻度，预设即刻度标签，指针可拖动。
/// 拖动吸附到 0.1″，靠近预设 ±0.1″ 时磁吸。
/// </summary>
public sealed class RulerControl : FrameworkElement
{
    private const double Pad = 8;           // 左右留白（给指针把手）
    private const double TickTop = 10, Base = 30;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RulerControl),
        new FrameworkPropertyMetadata(24.5, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((RulerControl)d).ValueChanged?.Invoke(d, (double)e.NewValue)));
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), typeof(double), 21.0);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), 27.0);
    public static readonly DependencyProperty TickBrushProperty = Reg(nameof(TickBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty StrongBrushProperty = Reg(nameof(StrongBrush), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty LabelBrushProperty = Reg(nameof(LabelBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty AccentBrushProperty = Reg(nameof(AccentBrush), typeof(Brush), Brushes.Red);
    public static readonly DependencyProperty SelectedPresetProperty = Reg(nameof(SelectedPreset), typeof(double), double.NaN);

    private static DependencyProperty Reg(string n, Type t, object def) =>
        DependencyProperty.Register(n, t, typeof(RulerControl), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public Brush StrongBrush { get => (Brush)GetValue(StrongBrushProperty); set => SetValue(StrongBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    /// <summary>当前高亮的预设（NaN = 无），由外部根据是否为分辨率直选决定。</summary>
    public double SelectedPreset { get => (double)GetValue(SelectedPresetProperty); set => SetValue(SelectedPresetProperty, value); }

    public double[] Presets { get; set; } = { 21.5, 23, 23.8, 24, 24.5, 25 };
    /// <summary>标签放第二行，避免相邻预设重叠。</summary>
    public double[] LowRow { get; set; } = { 23.8, 24.5 };
    public double ProValue { get; set; } = 24.5;

    public event EventHandler<double>? ValueChanged;
    public event EventHandler<double>? PresetClicked;

    private bool _drag;
    private readonly List<(Rect r, double v)> _labelRects = new();

    public RulerControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        Height = 74;
    }

    private double Span => Math.Max(0.1, Maximum - Minimum);
    private double X(double v) => Pad + (v - Minimum) / Span * Math.Max(1, ActualWidth - Pad * 2);

    private double ValueAt(double x)
    {
        double k = Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - Pad * 2), 0, 1);
        double v = Math.Round((Minimum + k * Span) * 10) / 10;
        foreach (var p in Presets) if (Math.Abs(p - v) <= 0.1 + 1e-9) return p;
        return v;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // 整块可点
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tf = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var tfB = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var thin = new Pen(TickBrush, 1); thin.Freeze();
        var mid = new Pen(StrongBrush, 1) ; var big = new Pen(StrongBrush, 1);
        // 刻度
        int from = (int)Math.Ceiling(Minimum * 10), to = (int)Math.Floor(Maximum * 10);
        for (int i = from; i <= to; i++)
        {
            double x = Math.Round(X(i / 10.0)) + 0.5;
            bool L = i % 10 == 0, M = i % 5 == 0;
            double h = L ? 18 : M ? 12 : 6;
            var pen = L ? big : M ? mid : thin;
            dc.PushOpacity(L ? 0.55 : M ? 0.3 : 1);
            dc.DrawLine(pen, new Point(x, Base - h), new Point(x, Base));
            dc.Pop();
        }
        dc.DrawRectangle(TickBrush, null, new Rect(Pad, Base, Math.Max(0, ActualWidth - Pad * 2), 1));

        // 已选填充
        double vx = X(Math.Clamp(Value, Minimum, Maximum));
        dc.DrawRectangle(AccentBrush, null, new Rect(Pad, Base - 1, Math.Max(0, vx - Pad), 3));

        // PRO 标记
        if (ProValue > Minimum && ProValue < Maximum)
        {
            var pro = new FormattedText("PRO", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tfB, 8.5, AccentBrush, ppd);
            dc.DrawText(pro, new Point(X(ProValue) - pro.Width / 2, -2));
        }

        // 预设标签
        _labelRects.Clear();
        foreach (var p in Presets)
        {
            if (p <= Minimum || p >= Maximum) continue;
            bool on = !double.IsNaN(SelectedPreset) && Math.Abs(SelectedPreset - p) < 0.05;
            bool low = LowRow.Contains(p);
            var ft = new FormattedText(p.ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, on ? tfB : tf, 12, on ? AccentBrush : LabelBrush, ppd);
            double x = X(p), y = low ? 52 : 38;
            if (low) dc.DrawRectangle(TickBrush, null, new Rect(Math.Round(x), 47, 1, 4));
            dc.DrawText(ft, new Point(x - ft.Width / 2, y));
            _labelRects.Add((new Rect(x - ft.Width / 2 - 4, y - 2, ft.Width + 8, ft.Height + 4), p));
        }
        var nat = new FormattedText("NATIVE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 9.5, LabelBrush, ppd);
        dc.DrawText(nat, new Point(ActualWidth - Pad - nat.Width + 4, 40));

        // 指针 + 把手
        dc.DrawRectangle(AccentBrush, null, new Rect(vx - 1, 4, 2, 32));
        var knob = new StreamGeometry();
        using (var c = knob.Open())
        {
            c.BeginFigure(new Point(vx, 32), true, true);
            c.PolyLineTo(new[] { new Point(vx + 8, 37), new Point(vx + 8, 44), new Point(vx - 8, 44), new Point(vx - 8, 37) }, false, false);
        }
        dc.DrawGeometry(AccentBrush, null, knob);

        // 键盘焦点框（虚线框禁用，改强调色细描边）
        if (IsKeyboardFocused)
            dc.DrawRectangle(null, new Pen(AccentBrush, 1.2), new Rect(0.5, 0.5, Math.Max(1, ActualWidth - 1), Math.Max(1, ActualHeight - 1)));
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        foreach (var (r, v) in _labelRects)
            if (r.Contains(p)) { PresetClicked?.Invoke(this, v); e.Handled = true; return; }
        _drag = true;
        CaptureMouse();
        Value = ValueAt(p.X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) Value = ValueAt(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = false;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            Value = Math.Clamp(Math.Round((Value + (e.Key == Key.Right ? 0.1 : -0.1)) * 10) / 10, Minimum, Maximum);
            e.Handled = true;
        }
    }
}
