using System.Diagnostics;
using System.Windows.Media;

namespace ScreenSizer.Controls;

/// <summary>基于 CompositionTarget.Rendering 的轻量补间，代码驱动动画用。</summary>
public sealed class Tween
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly double _ms;
    private readonly Func<double, double> _ease;
    private readonly Action<double> _step;
    private readonly Action? _done;
    private bool _stopped;

    private Tween(double ms, Func<double, double> ease, Action<double> step, Action? done)
    {
        _ms = Math.Max(1, ms); _ease = ease; _step = step; _done = done;
        CompositionTarget.Rendering += Tick;
        step(0);
    }

    public static Tween Run(double ms, Func<double, double> ease, Action<double> step, Action? done = null)
        => new(ms, ease, step, done);

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? s, EventArgs e)
    {
        double k = Math.Min(1, _sw.Elapsed.TotalMilliseconds / _ms);
        _step(_ease(k));
        if (k >= 1) { Stop(); _done?.Invoke(); }
    }

    public static double Linear(double k) => k;
    public static double OutCubic(double k) => 1 - Math.Pow(1 - k, 3);
    public static double InOutCubic(double k) => k < .5 ? 4 * k * k * k : 1 - Math.Pow(-2 * k + 2, 3) / 2;
    /// <summary>快进慢出 + 轻微回弹，用于「咔」一下锁定。</summary>
    public static double SnapBack(double k)
    {
        const double c1 = 1.2, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(k - 1, 3) + c1 * Math.Pow(k - 1, 2);
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
