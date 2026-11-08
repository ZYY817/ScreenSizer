using System.Management;

namespace ScreenSizer.Services;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

internal static class GpuService
{
    public sealed record Gpu(GpuVendor Vendor, string Name);

    public static List<Gpu> GetGpus()
    {
        var list = new List<Gpu>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                var pnp = o["PNPDeviceID"]?.ToString() ?? "";
                var name = o["Name"]?.ToString() ?? "";
                var vendor = pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                    : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || pnp.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                    : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                    : GpuVendor.Unknown;
                list.Add(new Gpu(vendor, name));
            }
        }
        catch { }
        return list;
    }

    /// <summary>主显示器所连显卡的最佳猜测（v1：取第一个独显；核显+独显笔记本场景再细分）。</summary>
    public static Gpu PrimaryGpu()
    {
        var gpus = GetGpus();
        return gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia)
               ?? gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Amd)
               ?? gpus.FirstOrDefault()
               ?? new Gpu(GpuVendor.Unknown, "未知");
    }
}
