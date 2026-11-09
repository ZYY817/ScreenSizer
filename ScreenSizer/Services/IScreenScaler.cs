using ScreenSizer.Models;

namespace ScreenSizer.Services;

public sealed record ScaleResult(bool Ok, string Message);

/// <summary>
/// 整屏缩放后端。N 卡走 NVAPI 自定义分辨率（NvidiaScaler），
/// AMD / Intel 等走系统 CCD 居中缩放 + 现成分辨率（CcdScaler）。
/// 界面按显卡自动选择；AppliedState.Backend 记录用过的后端，还原时找回同一个。
/// </summary>
internal interface IScreenScaler
{
    /// <summary>后端标识，写进 AppliedState.Backend。</summary>
    string Backend { get; }

    /// <summary>该后端在当前机器上可用。</summary>
    bool IsSupported { get; }

    /// <summary>把期望分辨率调整为后端实际可应用的值；null = 原样可用。</summary>
    (int W, int H)? SnapTarget(DisplayInfo d, int w, int h);

    /// <summary>「按分辨率选择」的候选分辨率；null = 不限制（后端能造任意分辨率）。</summary>
    IReadOnlyList<(int W, int H)>? AvailableTargets(DisplayInfo d);

    /// <summary>缩到 w×h 居中显示。</summary>
    ScaleResult Apply(DisplayInfo d, int w, int h);

    /// <summary>恢复原生分辨率 + 原缩放模式。</summary>
    ScaleResult Restore();

    /// <summary>应急还原：不依赖保存的状态，强制回到原生设置。</summary>
    ScaleResult ForceRestore(DisplayInfo d);
}
