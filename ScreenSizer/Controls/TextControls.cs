using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScreenSizer.Controls;

/// <summary>带字距的单行文字（WPF TextBlock 不支持 letter-spacing）。可带一个强调色前缀，如「01 MODE」。</summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty PrefixProperty = Reg(nameof(Prefix), typeof(string), "");
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 10.5);
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Normal);
    public static readonly DependencyProperty FontFamilyProperty = Reg(nameof(FontFamily), typeof(FontFamily), new FontFamily("Bahnschrift, Segoe UI"));
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty PrefixBrushProperty = Reg(nameof(PrefixBrush), typeof(Brush), Brushes.Red);
    public static readonly DependencyProperty TrackingProperty = Reg(nameof(Tracking), typeof(double), 0.16);

    private static DependencyProperty Reg(string n, Type t, object def) =>
        DependencyProperty.Register(n, t, typeof(TrackedText),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush PrefixBrush { get => (Brush)GetValue(PrefixBrushProperty); set => SetValue(PrefixBrushProperty, value); }
    /// <summary>字距，单位 em。</summary>
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }

    private IEnumerable<(FormattedText ft, double x)> Layout(out double width, out double height)
    {
        var list = new List<(FormattedText, double)>();
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0, h = 0, track = Tracking * FontSize;
        void Run(string s, Brush b, FontWeight w)
        {
            var tf = new Typeface(FontFamily, FontStyles.Normal, w, FontStretches.Normal);
            foreach (var ch in s)
            {
                var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, FontSize, b, ppd);
                list.Add((ft, x));
                x += ft.WidthIncludingTrailingWhitespace + track;
                h = Math.Max(h, ft.Height);
            }
        }
        if (!string.IsNullOrEmpty(Prefix)) { Run(Prefix, PrefixBrush, FontWeights.SemiBold); x += FontSize * 0.5; }
        Run(Text ?? "", Foreground, FontWeight);
        width = Math.Max(0, x - track);
        height = h;
        return list;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Layout(out var w, out var h);
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (ft, x) in Layout(out _, out _)) dc.DrawText(ft, new Point(x, 0));
    }
}

/// <summary>描边文字（艺术层大字用）。</summary>
public sealed class OutlineText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 100.0);
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Bold);
    public static readonly DependencyProperty FontFamilyProperty = Reg(nameof(FontFamily), typeof(FontFamily), new FontFamily("Bahnschrift, Segoe UI"));
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), null!);
    public static readonly DependencyProperty StrokeThicknessProperty = Reg(nameof(StrokeThickness), typeof(double), 1.0);

    private static DependencyProperty Reg(string n, Type t, object? def) =>
        DependencyProperty.Register(n, t, typeof(OutlineText),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private FormattedText Make() => new(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Brushes.Black,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = Make();
        return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geo = Make().BuildGeometry(new Point(0, 0));
        dc.DrawGeometry(Fill, Stroke != null ? new Pen(Stroke, StrokeThickness) : null, geo);
    }
}

/// <summary>右上角 45° 切角（附加属性）。</summary>
public static class Cut
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(Cut), new PropertyMetadata(0.0, OnChanged));

    public static double GetSize(DependencyObject o) => (double)o.GetValue(SizeProperty);
    public static void SetSize(DependencyObject o, double v) => o.SetValue(SizeProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }

    private static void Update(object s, SizeChangedEventArgs e) => Apply((FrameworkElement)s);

    private static void Apply(FrameworkElement fe)
    {
        double c = GetSize(fe), w = fe.ActualWidth, h = fe.ActualHeight;
        if (c <= 0 || w <= 0 || h <= 0) { fe.Clip = null; return; }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true, true);
            ctx.PolyLineTo(new[] { new Point(w - c, 0), new Point(w, c), new Point(w, h), new Point(0, h) }, false, false);
        }
        g.Freeze();
        fe.Clip = g;
    }
}
