using ScreenSizer.Models;

namespace ScreenSizer.Services;

/// <summary>目标英寸 → 目标分辨率。等比缩放到对角线比例，取偶数。</summary>
internal static class SizeCalculator
{
    public static (int W, int H) ResolutionFor(DisplayInfo d, double targetInches)
    {
        double diag = d.DiagonalInches;
        if (diag <= 0)
            throw new InvalidOperationException("无法读取显示器物理尺寸（EDID 缺失），请改用分辨率输入模式");

        double scale = targetInches / diag;
        if (scale > 1.0) scale = 1.0;
        if (scale < 0.3) scale = 0.3;

        int w = Even((int)Math.Round(d.NativeWidth * scale));
        int h = Even((int)Math.Round(d.NativeHeight * scale));
        if (w < 800 || h < 600) throw new InvalidOperationException($"目标分辨率 {w}×{h} 过小");
        return (w, h);
    }

    private static int Even(int v) => v & ~1;

    /// <summary>反推：给定分辨率在当前屏上的等效对角线英寸。</summary>
    public static double InchesFor(DisplayInfo d, int w, int h)
    {
        double diagPx = Math.Sqrt((double)d.NativeWidth * d.NativeWidth + (double)d.NativeHeight * d.NativeHeight);
        double targetPx = Math.Sqrt((double)w * w + (double)h * h);
        return d.DiagonalInches * targetPx / diagPx;
    }
}
