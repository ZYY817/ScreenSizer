using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScreenSizer.Services;

/// <summary>明暗主题 + 强调色。令牌字典放在 MergedDictionaries[0]，运行时替换即可，所有 DynamicResource 自动刷新。</summary>
internal static class ThemeService
{
    public sealed record Accent(string Name, Color Color, Color Ink);

    public static readonly Accent[] Accents =
    {
        new("竞技红", Color.FromRgb(0xFF, 0x3B, 0x4E), Colors.White),
        new("信号青", Color.FromRgb(0x00, 0xB4, 0xF0), Colors.White),
        new("荧光绿", Color.FromRgb(0x2F, 0xCB, 0x74), Color.FromRgb(0x06, 0x24, 0x0F)),
        new("琥珀",   Color.FromRgb(0xFF, 0xAE, 0x1A), Color.FromRgb(0x2B, 0x1A, 0x00)),
        new("电光紫", Color.FromRgb(0x8B, 0x5C, 0xF6), Colors.White),
    };

    public static string Mode { get; private set; } = "system";
    public static int AccentIndex { get; private set; }
    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    private static bool _hooked;

    public static void Apply(string mode, int accent)
    {
        Mode = mode is "light" or "dark" ? mode : "system";
        AccentIndex = Math.Clamp(accent, 0, Accents.Length - 1);
        IsDark = Mode == "dark" || (Mode == "system" && SystemIsDark());

        var res = Application.Current.Resources;
        var tokens = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Tokens.{(IsDark ? "Dark" : "Light")}.xaml")
        };
        if (res.MergedDictionaries.Count > 0) res.MergedDictionaries[0] = tokens;
        else res.MergedDictionaries.Add(tokens);

        var a = Accents[AccentIndex];
        res["AccentColor"] = a.Color;
        res["AccentGlowColor"] = WithAlpha(a.Color, (byte)(IsDark ? 0x1C : 0x17));
        res["AccentClearColor"] = WithAlpha(a.Color, 0);
        res["AccentBrush"] = Frozen(a.Color);
        res["AccentInkBrush"] = Frozen(a.Ink);
        res["AccentSoftBrush"] = Frozen(WithAlpha(a.Color, 0x21));
        res["AccentLineBrush"] = Frozen(WithAlpha(a.Color, 0x73));
        res["AccentSweepBrush"] = Frozen(WithAlpha(a.Color, 0x4D));

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == "system")
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(Mode, AccentIndex));
            };
        }
        Changed?.Invoke();
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
