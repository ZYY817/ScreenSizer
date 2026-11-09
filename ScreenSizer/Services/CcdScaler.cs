using System.Runtime.InteropServices;
using ScreenSizer.Models;
using ScreenSizer.Native;

namespace ScreenSizer.Services;

/// <summary>
/// 通用后端（AMD / Intel / 无 NVAPI）：Windows CCD 居中缩放 + 驱动已支持的分辨率。
/// 不能创建自定义分辨率，目标只能从 EnumDisplaySettingsEx 列出的模式里选；
/// 缩放模式通过 SetDisplayConfig 设为 DISPLAYCONFIG_SCALING_CENTERED → 居中、四边黑边。
/// </summary>
internal sealed class CcdScaler : IScreenScaler
{
    private readonly Action<string> _log;

    public CcdScaler(Action<string> log) => _log = log;

    public string Backend => "ccd";

    private bool? _supported;
    public bool IsSupported => _supported ??= Probe();

    private bool Probe()
    {
        try
        {
            int rc = CcdApi.GetDisplayConfigBufferSizes(CcdApi.QDC_DATABASE_CURRENT, out uint pc, out _);
            _log($"CCD 探测 rc={rc} paths={pc}");
            return rc == CcdApi.ERROR_SUCCESS && pc > 0;
        }
        catch (Exception e)
        {
            _log($"CCD 探测异常: {e.Message}");
            return false;
        }
    }

    // ---------- 分辨率枚举与吸附 ----------

    /// <summary>该显示器所有 32bpp 模式（含刷新率）。</summary>
    public static List<(int W, int H, uint Hz)> EnumerateModes(string deviceName)
    {
        var list = new List<(int, int, uint)>();
        var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
        for (int i = 0; NativeMethods.EnumDisplaySettingsEx(deviceName, i, ref dm, 0); i++)
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth >= 800 && dm.dmPelsHeight >= 600)
                list.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, dm.dmDisplayFrequency));
        return list;
    }

    /// <summary>可用于缩小的候选分辨率：同比例、小于原生，按对角线从大到小。</summary>
    public IReadOnlyList<(int W, int H)>? AvailableTargets(DisplayInfo d)
    {
        double aspect = (double)d.NativeWidth / d.NativeHeight;
        var set = new SortedSet<(int W, int H)>(Comparer<(int W, int H)>.Create(
            (a, b) => (b.W * b.W + b.H * b.H).CompareTo(a.W * a.W + a.H * a.H) is var c && c != 0 ? c : b.W.CompareTo(a.W)));
        foreach (var (w, h, _) in EnumerateModes(d.DeviceName))
        {
            if (w >= d.NativeWidth || h >= d.NativeHeight) continue;
            if (Math.Abs((double)w / h - aspect) > 0.015) continue;
            set.Add((w, h));
        }
        return set.ToList();
    }

    /// <summary>期望分辨率 → 对角线最近的可用模式。</summary>
    public (int W, int H)? SnapTarget(DisplayInfo d, int w, int h)
    {
        var modes = AvailableTargets(d);
        if (modes == null || modes.Count == 0) return null;
        double want = Math.Sqrt((double)w * w + h * h);
        var best = modes.OrderBy(m => Math.Abs(Math.Sqrt(m.W * m.W + m.H * m.H) - want)).First();
        return best == (w, h) ? null : best;
    }

    /// <summary>(w,h) 对应的最佳刷新率：优先等于当前刷新率，否则取最高。</summary>
    private static uint BestHz(string deviceName, int w, int h, float preferHz)
        => EnumerateModes(deviceName)
            .Where(m => m.W == w && m.H == h)
            .OrderByDescending(m => Math.Abs(m.Hz - preferHz) < 0.6 ? 1 : 0)
            .ThenByDescending(m => m.Hz)
            .Select(m => m.Hz)
            .FirstOrDefault();

    // ---------- IScreenScaler ----------

    public ScaleResult Apply(DisplayInfo d, int w, int h)
    {
        try
        {
            uint hz = BestHz(d.DeviceName, w, h, d.RefreshHz);
            if (hz == 0)
                return new ScaleResult(false, $"{w}×{h} 不是驱动支持的分辨率（通用后端只能用现成模式）");

            var existing = StateStore.Load();
            if (existing != null && (existing.TargetW != w || existing.TargetH != h))
                return new ScaleResult(false, $"已有应用中的缩放（{existing.TargetW}×{existing.TargetH}），请先「还原」再改尺寸");

            if (existing == null)
            {
                // 只在首次应用时记录原始缩放模式
                if (!TryQuery(out var paths0, out _) || FindPath(paths0, d.DeviceName) < 0)
                    return new ScaleResult(false, "QueryDisplayConfig 找不到该显示器");
                uint curScaling = (uint)paths0[FindPath(paths0, d.DeviceName)].targetInfo.scaling;
                _log($"当前缩放模式 = {curScaling}");
                StateStore.Save(new AppliedState
                {
                    DeviceName = d.DeviceName,
                    Backend = Backend,
                    TargetW = w,
                    TargetH = h,
                    SavedScaling = curScaling,
                    NativeW = d.NativeWidth,
                    NativeH = d.NativeHeight,
                    RefreshHz = d.RefreshHz,
                });
            }
            else _log("重设已应用的目标模式");

            // 1. 分辨率（驱动现成模式）
            SetResolution(d.DeviceName, w, h, hz);

            // 2. 缩放模式 → 居中
            int rc = ApplyScaling(d.DeviceName, CcdApi.DISPLAYCONFIG_SCALING.CENTERED);
            if (rc != CcdApi.ERROR_SUCCESS)
            {
                _log($"SetDisplayConfig 失败 {ErrorText(rc)}，回滚中");
                Rollback(d);
                return new ScaleResult(false, $"系统缩放设置失败（{ErrorText(rc)}），可改用「仅游戏窗口」模式");
            }

            // 3. 校验：分辨率 + 缩放模式都要到位
            var (cw, ch) = CurrentRes(d.DeviceName);
            var curScalingNow = QueryScaling(d.DeviceName);
            if (cw != w || ch != h || curScalingNow != CcdApi.DISPLAYCONFIG_SCALING.CENTERED)
            {
                _log($"校验失败：{cw}×{ch} 缩放={curScalingNow}，回滚中");
                Rollback(d);
                return new ScaleResult(false, "该驱动不支持居中缩放（校验未通过），可改用「仅游戏窗口」模式");
            }

            return new ScaleResult(true, $"{w}×{h}@{hz} 已居中显示（等效约 {SizeCalculator.InchesFor(d, w, h):0.#}″ · 系统缩放）");
        }
        catch (Exception e)
        {
            try { Rollback(d); } catch { }
            return new ScaleResult(false, e.Message + "（已回滚）");
        }
    }

    public ScaleResult Restore()
    {
        try
        {
            var st = StateStore.Load();
            if (st == null) return new ScaleResult(false, "没有已保存的应用状态");

            SetResolution(st.DeviceName, st.NativeW, st.NativeH, st.RefreshHz);
            int rc = ApplyScaling(st.DeviceName, (CcdApi.DISPLAYCONFIG_SCALING)st.SavedScaling);
            if (rc != CcdApi.ERROR_SUCCESS) _log($"还原缩放模式 {ErrorText(rc)}（可忽略）");

            var (cw, ch) = CurrentRes(st.DeviceName);
            if (cw != st.NativeW || ch != st.NativeH)
            {
                _log($"校验发现 {cw}×{ch}，再压一次原生模式");
                SetResolution(st.DeviceName, st.NativeW, st.NativeH, st.RefreshHz);
                (cw, ch) = CurrentRes(st.DeviceName);
            }
            if (cw != st.NativeW || ch != st.NativeH)
                return new ScaleResult(false, $"分辨率仍停在 {cw}×{ch}——请用「应急还原」或重启电脑");

            StateStore.Clear();
            return new ScaleResult(true, $"已还原 {st.NativeW}×{st.NativeH}@{st.RefreshHz:0}Hz");
        }
        catch (Exception e)
        {
            return new ScaleResult(false, e.Message);
        }
    }

    public ScaleResult ForceRestore(DisplayInfo d)
    {
        try
        {
            SetResolution(d.DeviceName, d.NativeWidth, d.NativeHeight, d.RefreshHz);
            // 有记录就还原原缩放，没有就用「系统偏好」（原生分辨率下缩放模式无视觉差异）
            var fallback = StateStore.Load()?.SavedScaling is { } s
                ? (CcdApi.DISPLAYCONFIG_SCALING)s
                : CcdApi.DISPLAYCONFIG_SCALING.PREFERRED;
            int rc = ApplyScaling(d.DeviceName, fallback);
            if (rc != CcdApi.ERROR_SUCCESS) _log($"缩放复位 {ErrorText(rc)}（可忽略）");

            var (cw, ch) = CurrentRes(d.DeviceName);
            if (cw != d.NativeWidth || ch != d.NativeHeight)
            {
                SetResolution(d.DeviceName, d.NativeWidth, d.NativeHeight, d.RefreshHz);
                (cw, ch) = CurrentRes(d.DeviceName);
            }

            StateStore.Clear();
            return cw == d.NativeWidth && ch == d.NativeHeight
                ? new ScaleResult(true, $"已强制恢复 {cw}×{ch}")
                : new ScaleResult(false, $"仍停在 {cw}×{ch}，请重启电脑或在系统设置里改回分辨率");
        }
        catch (Exception e)
        {
            return new ScaleResult(false, e.Message);
        }
    }

    // ---------- 内部工具 ----------

    /// <summary>尽力回到原生分辨率 + 原缩放模式（应用失败回滚用）。</summary>
    private void Rollback(DisplayInfo d)
    {
        var st = StateStore.Load();
        try { SetResolution(d.DeviceName, st?.NativeW ?? d.NativeWidth, st?.NativeH ?? d.NativeHeight, st?.RefreshHz ?? d.RefreshHz); } catch { }
        if (st != null)
            try { ApplyScaling(d.DeviceName, (CcdApi.DISPLAYCONFIG_SCALING)st.SavedScaling); } catch { }
        StateStore.Clear();
    }

    private static bool TryQuery(out CcdApi.DISPLAYCONFIG_PATH_INFO[] paths, out CcdApi.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<CcdApi.DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<CcdApi.DISPLAYCONFIG_MODE_INFO>();
        int rc = CcdApi.GetDisplayConfigBufferSizes(CcdApi.QDC_DATABASE_CURRENT, out uint pc, out uint mc);
        if (rc != CcdApi.ERROR_SUCCESS || pc == 0) return false;
        paths = new CcdApi.DISPLAYCONFIG_PATH_INFO[pc];
        modes = new CcdApi.DISPLAYCONFIG_MODE_INFO[mc];
        rc = CcdApi.QueryDisplayConfig(CcdApi.QDC_DATABASE_CURRENT, ref pc, paths, ref mc, modes, out _);
        if (rc != CcdApi.ERROR_SUCCESS) return false;
        Array.Resize(ref paths, (int)pc);
        Array.Resize(ref modes, (int)mc);
        return true;
    }

    /// <summary>path.sourceInfo → GDI 设备名（\\.\DISPLAY1）。</summary>
    private static string SourceName(in CcdApi.DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new CcdApi.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new CcdApi.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = CcdApi.DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<CcdApi.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };
        return CcdApi.DisplayConfigGetDeviceInfo(ref name) == CcdApi.ERROR_SUCCESS ? name.viewGdiDeviceName : "";
    }

    /// <summary>在 paths 里找该显示器的活动路径索引。</summary>
    private static int FindPath(CcdApi.DISPLAYCONFIG_PATH_INFO[] paths, string deviceName)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if ((paths[i].flags & CcdApi.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            if (SourceName(paths[i]).Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>只改缩放模式：重查当前数据库配置 → 改 scaling → SetDisplayConfig 应用并入库。</summary>
    private int ApplyScaling(string deviceName, CcdApi.DISPLAYCONFIG_SCALING scaling)
    {
        if (!TryQuery(out var paths, out var modes)) return -1;
        int i = FindPath(paths, deviceName);
        if (i < 0) return -1;
        paths[i].targetInfo.scaling = scaling;
        int rc = CcdApi.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            CcdApi.SDC_APPLY | CcdApi.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdApi.SDC_SAVE_TO_DATABASE | CcdApi.SDC_ALLOW_CHANGES);
        _log($"SetDisplayConfig scaling={scaling} → {deviceName} rc={rc} {ErrorText(rc)}");
        return rc;
    }

    private static CcdApi.DISPLAYCONFIG_SCALING QueryScaling(string deviceName)
    {
        if (!TryQuery(out var paths, out _)) return 0;
        int i = FindPath(paths, deviceName);
        return i < 0 ? 0 : paths[i].targetInfo.scaling;
    }

    private void SetResolution(string deviceName, int w, int h, float hz)
    {
        var dm = new NativeMethods.DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>(),
            dmPelsWidth = (uint)w,
            dmPelsHeight = (uint)h,
            dmBitsPerPel = 32,
            dmDisplayFrequency = (uint)Math.Round(hz),
            dmFields = NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT
                     | NativeMethods.DM_BITSPERPEL | NativeMethods.DM_DISPLAYFREQUENCY,
        };
        int rc = NativeMethods.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero,
            NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_GLOBAL, IntPtr.Zero);
        _log($"设置 {w}×{h}@{Math.Round(hz)}Hz rc={rc}");
        if (rc != NativeMethods.DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"ChangeDisplaySettingsEx 失败 rc={rc}");
    }

    private static (int W, int H) CurrentRes(string deviceName)
    {
        var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
        return NativeMethods.EnumDisplaySettingsEx(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0)
            ? ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight) : (0, 0);
    }

    private static string ErrorText(int rc) => rc switch
    {
        0 => "SUCCESS",
        5 => "ACCESS_DENIED",
        31 => "GEN_FAILURE",
        50 => "NOT_SUPPORTED",
        87 => "INVALID_PARAMETER",
        122 => "INSUFFICIENT_BUFFER",
        1610 => "BAD_CONFIGURATION",
        _ => $"error {rc}",
    };
}
