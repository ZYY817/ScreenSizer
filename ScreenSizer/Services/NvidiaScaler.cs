using System.Runtime.InteropServices;
using ScreenSizer.Models;
using ScreenSizer.Native;

namespace ScreenSizer.Services;

public sealed record ScaleResult(bool Ok, string Message);

/// <summary>
/// NVIDIA 驱动级缩放：自定义分辨率 + GPU 居中无缩放。
/// 复刻 NVIDIA 控制面板"调整桌面尺寸和位置"的效果。
/// </summary>
internal sealed class NvidiaScaler
{
    private readonly Action<string> _log;

    public NvidiaScaler(Action<string> log) => _log = log;

    public bool IsSupported => NvApi.IsAvailable;

    /// <summary>把 display 缩到 w×h 居中显示。</summary>
    public ScaleResult Apply(DisplayInfo d, int w, int h)
    {
        try
        {
            NvApi.Initialize();
            uint displayId = NvApi.GetDisplayId(d.DeviceName);
            _log($"displayId = 0x{displayId:x8}");

            // 已应用状态：目标不同→要求先还原；目标相同→重设（保留原始缩放记录）
            var existing = StateStore.Load();
            if (existing != null && (existing.TargetW != w || existing.TargetH != h))
                return new ScaleResult(false, $"已有应用中的缩放（{existing.TargetW}×{existing.TargetH}），请先「还原」再改尺寸");

            if (existing == null)
            {
                // 只在首次应用时记录原始缩放——否则会把我们自己设的"无缩放"存成原始值
                uint? curScaling = NvApi.GetScaling(displayId);
                _log($"当前缩放模式 = {curScaling?.ToString() ?? "未知"}");
                StateStore.Save(new AppliedState
                {
                    DeviceName = d.DeviceName,
                    DisplayId = displayId,
                    TargetW = w,
                    TargetH = h,
                    SavedScaling = curScaling ?? NvApi.NV_SCALING_GPU_SCALING_TO_NATIVE,
                    NativeW = d.NativeWidth,
                    NativeH = d.NativeHeight,
                    RefreshHz = d.RefreshHz,
                });
            }
            else _log("重设已应用的目标模式");

            // 生成时序 + 创建自定义分辨率
            var timing = NvApi.GetTiming(displayId, (uint)w, (uint)h, d.RefreshHz);
            var cust = NvApi.MakeCustomDisplay((uint)w, (uint)h, timing);
            NvApi.TryCustomDisplay(displayId, ref cust);
            _log($"试用 {w}×{h}@{d.RefreshHz:0} 成功");

            // 持久化进注册表（出现在系统分辨率列表）
            try { NvApi.SaveCustomDisplay(displayId); _log("自定义分辨率已保存"); }
            catch (Exception e) { _log($"SaveCustomDisplay 跳过: {e.Message}"); }

            // 缩放模式改为"居中/无缩放" → 物理缩小、四周黑边
            NvApi.SetScaling(displayId, NvApi.NV_SCALING_GPU_SCANOUT_TO_NATIVE);
            _log("缩放模式已设为 居中无缩放");

            // 校验实际分辨率，失败则回滚
            var (cw, ch) = CurrentRes(d.DeviceName);
            if (cw != w || ch != h)
            {
                _log($"校验失败：当前 {cw}×{ch} ≠ {w}×{h}，回滚中");
                try { NvApi.RevertCustomDisplayTrial(displayId); } catch { }
                var st = StateStore.Load();
                try { NvApi.SetScaling(displayId, st?.SavedScaling ?? NvApi.NV_SCALING_GPU_SCALING_TO_NATIVE); } catch { }
                try { SetResolution(d.DeviceName, d.NativeWidth, d.NativeHeight, d.RefreshHz); } catch { }
                return new ScaleResult(false, $"应用未生效（当前 {cw}×{ch}），已自动回滚");
            }

            return new ScaleResult(true, $"{w}×{h} 已居中显示（等效约 {SizeCalculator.InchesFor(d, w, h):0.#}\"）");
        }
        catch (Exception e)
        {
            // 回滚：撤销试用 + 恢复原缩放 + 回原生分辨率
            try
            {
                var st = StateStore.Load();
                if (st != null)
                {
                    try { NvApi.RevertCustomDisplayTrial(st.DisplayId); } catch { }
                    try { NvApi.SetScaling(st.DisplayId, st.SavedScaling); } catch { }
                    try { SetResolution(d.DeviceName, d.NativeWidth, d.NativeHeight, d.RefreshHz); } catch { }
                    StateStore.Clear();
                }
            }
            catch { }
            return new ScaleResult(false, e.Message + "（已回滚）");
        }
    }

    /// <summary>恢复原生分辨率 + 原缩放模式 + 删除自定义分辨率。</summary>
    public ScaleResult Restore()
    {
        try
        {
            var st = StateStore.Load();
            if (st == null) return new ScaleResult(false, "没有已保存的应用状态");
            NvApi.Initialize();
            uint displayId = st.DisplayId != 0 ? st.DisplayId : NvApi.GetDisplayId(st.DeviceName);

            // 1. 先把 Windows 模式设回原生（显式 DEVMODE，不走注册表快照——
            //    SaveCustomDisplay 可能已把自定义模式写成持久模式）
            SetResolution(st.DeviceName, st.NativeW, st.NativeH, st.RefreshHz);

            // 2. 撤试用残留 + 从驱动配置删掉自定义模式
            //    ★ 必须在 SetScaling 之前做：SetScaling 是"捕获整套显示配置→整包写回"，
            //    配置里含源分辨率，若自定义模式还挂着，SetScaling 会把它重新灌回去。
            int rev = NvApi.RevertCustomDisplayTrial(displayId);
            if (rev != 0) _log($"RevertTrial: {NvApi.ErrorText(rev)}（已提交模式属正常）");
            TryDeleteCustom(displayId, st.TargetW, st.TargetH, st.RefreshHz);

            // 3. 还原缩放模式（此刻驱动配置已回到原生，整包写回是安全的）
            try { NvApi.SetScaling(displayId, st.SavedScaling); _log($"缩放模式恢复为 {st.SavedScaling}"); }
            catch (Exception e) { _log($"还原缩放模式失败: {e.Message}"); }

            // 4. 最终校验——若被任何步骤带偏，再压一次原生
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

    /// <summary>应急还原：不依赖保存的状态，强制回到原生分辨率 + 安全缩放。</summary>
    public ScaleResult ForceRestore(DisplayInfo d)
    {
        try
        {
            NvApi.Initialize();
            uint displayId = NvApi.GetDisplayId(d.DeviceName);

            SetResolution(d.DeviceName, d.NativeWidth, d.NativeHeight, d.RefreshHz);

            int rev = NvApi.RevertCustomDisplayTrial(displayId);
            if (rev != 0) _log($"RevertTrial: {NvApi.ErrorText(rev)}");
            DeleteAllCustom(displayId); // 应急：清掉所有残留自定义模式，再让 SetScaling 整包写回

            uint safeScaling = StateStore.Load()?.SavedScaling ?? NvApi.NV_SCALING_GPU_SCALING_TO_NATIVE;
            try { NvApi.SetScaling(displayId, safeScaling); } catch { }

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

    // ----- 内部工具 -----

    /// <summary>显式设置分辨率（构造完整 DEVMODE，不走注册表快照）。</summary>
    private void SetResolution(string deviceName, int w, int h, float hz)
    {
        var dm = new NativeMethods.DEVMODE
        {
            dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>(),
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
            throw new NvApi.NvApiException($"ChangeDisplaySettingsEx 失败 rc={rc}");
    }

    private static (int W, int H) CurrentRes(string deviceName)
    {
        var dm = new NativeMethods.DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
        return NativeMethods.EnumDisplaySettingsEx(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0)
            ? ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight) : (0, 0);
    }

    private void TryDeleteCustom(uint displayId, int w, int h, float hz)
    {
        try
        {
            var timing = NvApi.GetTiming(displayId, (uint)w, (uint)h, hz);
            var cust = NvApi.MakeCustomDisplay((uint)w, (uint)h, timing);
            NvApi.DeleteCustomDisplay(displayId, ref cust);
            _log("自定义分辨率已删除");
        }
        catch (Exception e) { _log($"删除自定义分辨率失败（可忽略）: {e.Message}"); }
    }

    /// <summary>枚举并删除该显示器上所有已保存的自定义模式（应急清理用）。</summary>
    private void DeleteAllCustom(uint displayId)
    {
        for (int guard = 0; guard < 16; guard++)
        {
            var probe = NvApi.MakeCustomDisplay(0, 0, new NvApi.NvTiming { Etc = new NvApi.NvTimingExt { Name = new byte[40] } });
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(probe));
            try
            {
                Marshal.StructureToPtr(probe, p, false);
                if (NvApi.EnumCustomDisplayRaw(displayId, 0, p) != 0) break;
                var c = Marshal.PtrToStructure<NvApi.NvCustomDisplay>(p);
                NvApi.DeleteCustomDisplay(displayId, ref c);
                _log($"删除残留自定义模式 {c.Width}×{c.Height}");
            }
            catch { break; }
            finally { Marshal.FreeHGlobal(p); }
        }
    }
}
