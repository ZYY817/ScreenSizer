using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenSizer.Native;

/// <summary>
/// 窗口外观：圆角 + 浅色标题栏。
/// 注意：Windows Acrylic/Mica 在"透明效果"关闭时全部失效，
/// 所以玻璃背景改由 XAML 里的壁纸模糊层实现（不依赖系统设置）。
/// </summary>
internal static class DwmHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;
        int corner = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4); } catch { }
        int dark = 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4); } catch { }
    }
}
