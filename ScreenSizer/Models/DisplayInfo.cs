namespace ScreenSizer.Models;

/// <summary>一块显示器的完整信息。</summary>
public sealed class DisplayInfo
{
    /// <summary>GDI 设备名，如 \\.\DISPLAY1（NVAPI GetDisplayIdByDisplayName 用它）</summary>
    public required string DeviceName { get; init; }

    /// <summary>显示器名（驱动报告的名称），如 AOC Q27G3XMN</summary>
    public string MonitorName { get; init; } = "";

    /// <summary>即插即用 ID 中的型号段，如 AOCB453</summary>
    public string PnpId { get; init; } = "";

    public string AdapterName { get; init; } = "";

    public bool IsPrimary { get; init; }

    // 原生（面板物理）分辨率：来自注册表设置
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }

    // 当前分辨率与刷新率
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public float RefreshHz { get; init; }

    // 物理尺寸（cm，来自 EDID/WMI）
    public float PhysWidthCm { get; init; }
    public float PhysHeightCm { get; init; }

    public double DiagonalInches =>
        PhysWidthCm > 0 && PhysHeightCm > 0
            ? Math.Sqrt(PhysWidthCm * PhysWidthCm + PhysHeightCm * PhysHeightCm) / 2.54
            : 0;

    /// <summary>给下拉框显示的友好名称。</summary>
    public string UiName { get; set; } = "";

    public override string ToString() =>
        !string.IsNullOrEmpty(UiName)
            ? UiName
            : $"{DeviceName}  {MonitorName}  {NativeWidth}×{NativeHeight}  ({DiagonalInches:0.#}\")";
}
