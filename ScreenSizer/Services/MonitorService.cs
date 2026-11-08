using System.Management;
using ScreenSizer.Models;
using ScreenSizer.Native;

namespace ScreenSizer.Services;

/// <summary>枚举已接入桌面布局的显示器：原生分辨率、当前模式、物理尺寸。</summary>
internal static class MonitorService
{
    private sealed record WmiMonitor(string PnpId, float Wcm, float Hcm, bool Active, string Instance);

    public static List<DisplayInfo> GetDisplays()
    {
        var wmi = QueryWmiMonitors();
        var list = new List<DisplayInfo>();

        var dd = new NativeMethods.DISPLAY_DEVICE { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var adapter = dd;
            if ((adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;

            // 该适配器上挂的显示器
            string pnpId = "", monitorName = "";
            var mon = new NativeMethods.DISPLAY_DEVICE { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            for (uint j = 0; NativeMethods.EnumDisplayDevices(adapter.DeviceName, j, ref mon, 0); j++)
            {
                // DeviceID 形如 MONITOR\AOCB453\5&368b9ad6&0&UID4353
                var parts = mon.DeviceID.Split('\\');
                if (parts.Length >= 3)
                {
                    pnpId = parts[1];
                    monitorName = mon.DeviceString;
                    break;
                }
            }

            var dmNative = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
            var dmCur = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
            bool hasNative = NativeMethods.EnumDisplaySettingsEx(adapter.DeviceName, NativeMethods.ENUM_REGISTRY_SETTINGS, ref dmNative, 0);
            bool hasCur = NativeMethods.EnumDisplaySettingsEx(adapter.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dmCur, 0);
            if (!hasCur) continue;

            var phys = wmi.FirstOrDefault(w => w.PnpId.Equals(pnpId, StringComparison.OrdinalIgnoreCase))
                       ?? wmi.FirstOrDefault(w => w.Active);

            list.Add(new DisplayInfo
            {
                DeviceName = adapter.DeviceName,
                MonitorName = monitorName,
                PnpId = pnpId,
                AdapterName = adapter.DeviceString,
                IsPrimary = (adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                NativeWidth = hasNative ? (int)dmNative.dmPelsWidth : (int)dmCur.dmPelsWidth,
                NativeHeight = hasNative ? (int)dmNative.dmPelsHeight : (int)dmCur.dmPelsHeight,
                CurrentWidth = (int)dmCur.dmPelsWidth,
                CurrentHeight = (int)dmCur.dmPelsHeight,
                RefreshHz = dmCur.dmDisplayFrequency,
                PhysWidthCm = phys?.Wcm ?? 0,
                PhysHeightCm = phys?.Hcm ?? 0,
            });
        }
        return list;
    }

    private static List<WmiMonitor> QueryWmiMonitors()
    {
        var result = new List<WmiMonitor>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize, Active FROM WmiMonitorBasicDisplayParams");
            foreach (var o in searcher.Get())
            {
                var inst = o["InstanceName"]?.ToString() ?? "";
                var parts = inst.Split('\\');
                var pnp = parts.Length >= 2 ? parts[1] : "";
                result.Add(new WmiMonitor(
                    pnp,
                    Convert.ToSingle(o["MaxHorizontalImageSize"] ?? 0),
                    Convert.ToSingle(o["MaxVerticalImageSize"] ?? 0),
                    Convert.ToBoolean(o["Active"] ?? false),
                    inst));
            }
        }
        catch { /* WMI 不可用时物理尺寸留空 */ }
        return result;
    }
}
