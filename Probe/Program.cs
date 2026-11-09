// CCD 通用后端实测：枚举模式 → 应用 → 保持 → 还原（Intel UHD 630 验证用）
// 用法：dotnet run --project Probe -c Debug -- [W] [H] [holdSeconds]
using ScreenSizer.Models;
using ScreenSizer.Native;
using ScreenSizer.Services;

int w = args.Length > 0 ? int.Parse(args[0]) : 1920;
int h = args.Length > 1 ? int.Parse(args[1]) : 1080;
int hold = args.Length > 2 ? int.Parse(args[2]) : 6;

// 以测试前的当前模式作为「原生」，还原时回到这里（不再写死分辨率，避免把用户屏幕改到别的档位）
const string Dev = @"\\.\DISPLAY1";
var cur = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
if (!NativeMethods.EnumDisplaySettingsEx(Dev, NativeMethods.ENUM_CURRENT_SETTINGS, ref cur, 0))
{
    Console.WriteLine("读取当前显示模式失败，已退出，未做任何改动");
    return;
}
Console.WriteLine($"测试前模式: {cur.dmPelsWidth}x{cur.dmPelsHeight}@{cur.dmDisplayFrequency}Hz（还原目标）");

var d = new DisplayInfo
{
    DeviceName = Dev,
    IsPrimary = true,
    NativeWidth = (int)cur.dmPelsWidth,
    NativeHeight = (int)cur.dmPelsHeight,
    CurrentWidth = (int)cur.dmPelsWidth,
    CurrentHeight = (int)cur.dmPelsHeight,
    RefreshHz = cur.dmDisplayFrequency,
    PhysWidthCm = 60,
    PhysHeightCm = 34,
};

var scaler = new CcdScaler(m => Console.WriteLine("  " + m));
Console.WriteLine($"IsSupported: {scaler.IsSupported}");

var avail = scaler.AvailableTargets(d);
Console.WriteLine($"可用模式 {avail?.Count ?? 0} 个：" + string.Join(" ", (avail ?? []).Select(m => $"{m.W}x{m.H}")));

Console.WriteLine($"吸附 {w}×{h} → {scaler.SnapTarget(d, w, h)?.ToString() ?? "无需吸附"}");

Console.WriteLine($"\n=== 应用 {w}×{h} 居中 ===");
var r = scaler.Apply(d, w, h);
Console.WriteLine($"Apply: {r.Ok} {r.Message}");

try
{
    if (r.Ok)
    {
        Console.WriteLine($"保持 {hold} 秒…");
        Thread.Sleep(hold * 1000);
    }
}
finally
{
    Console.WriteLine("\n=== 还原 ===");
    var rr = scaler.Restore();
    Console.WriteLine($"Restore: {rr.Ok} {rr.Message}");
    var fr = scaler.ForceRestore(d);
    Console.WriteLine($"ForceRestore: {fr.Ok} {fr.Message}");
}
