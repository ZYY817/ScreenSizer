// 全链路实测 v2：应用 → 保持 8 秒 → 还原（签名已修复）
using System.Runtime.InteropServices;
using ScreenSizer.Native;

const uint W = 2208, H = 1242;

NvApi.Initialize();
uint displayId = NvApi.GetDisplayId("\\\\.\\DISPLAY1");
uint? savedScaling = NvApi.GetScaling(displayId);
Console.WriteLine($"displayId: 0x{displayId:x8} 当前缩放: {savedScaling}");

try
{
    var timing = NvApi.GetTiming(displayId, W, H, 280f);
    var cust = NvApi.MakeCustomDisplay(W, H, timing);

    Console.WriteLine("3 秒后应用…");
    Thread.Sleep(3000);

    NvApi.TryCustomDisplay(displayId, ref cust);
    Console.WriteLine("✓ TryCustomDisplay");

    NvApi.SaveCustomDisplay(displayId);
    Console.WriteLine("✓ SaveCustomDisplay（已进分辨率列表）");

    NvApi.SetScaling(displayId, NvApi.NV_SCALING_GPU_SCANOUT_TO_NATIVE);
    Console.WriteLine("✓ 缩放 = 居中无缩放");

    var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
    NativeMethods.EnumDisplaySettingsEx("\\\\.\\DISPLAY1", NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0);
    Console.WriteLine($"当前模式: {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz — 保持 8 秒");
    Thread.Sleep(8000);
}
finally
{
    Console.WriteLine("还原中…");
    NvApi.RevertCustomDisplayTrial(displayId);
    if (savedScaling.HasValue) NvApi.SetScaling(displayId, savedScaling.Value);

    // 删除保存的自定义分辨率
    var timing = NvApi.GetTiming(displayId, W, H, 280f);
    var cust = NvApi.MakeCustomDisplay(W, H, timing);
    NvApi.DeleteCustomDisplay(displayId, ref cust);
    Console.WriteLine("✓ 已还原 + 已删除自定义分辨率");

    var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
    NativeMethods.EnumDisplaySettingsEx("\\\\.\\DISPLAY1", NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0);
    Console.WriteLine($"最终模式: {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz");
}
