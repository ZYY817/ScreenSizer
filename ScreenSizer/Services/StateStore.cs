using System.IO;
using System.Text.Json;

namespace ScreenSizer.Services;

public sealed class AppliedState
{
    public required string DeviceName { get; init; }
    public uint DisplayId { get; init; }
    /// <summary>写入该状态的后端："nvidia" / "ccd"。还原时按它找回同一个后端。</summary>
    public string Backend { get; init; } = "nvidia";
    public int TargetW { get; init; }
    public int TargetH { get; init; }
    public uint SavedScaling { get; init; }
    public int NativeW { get; init; }
    public int NativeH { get; init; }
    public float RefreshHz { get; init; }
    public DateTime AppliedAt { get; init; } = DateTime.Now;
}

public sealed class AppSettings
{
    public bool DriftDetect { get; set; } = true;

    // 外观
    public string Theme { get; set; } = "system";   // system / light / dark
    public int Accent { get; set; }
    public bool? ReduceMotion { get; set; }          // null = 跟随系统动画设置

    // 运行
    public bool MinimizeToTray { get; set; } = true;
    public bool ConfirmCountdown { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public string? LatestTag { get; set; }
    public bool TrayHintShown { get; set; }

    // 全局热键（形如 "Ctrl+Alt+S"）
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+S";
    public string HotkeyForce { get; set; } = "Ctrl+Alt+R";

    // 我的配置
    public List<Profile> Profiles { get; set; } = new()
    {
        new("CS2", 24.5), new("VALORANT", 24), new("APEX", 23.8),
    };
}

public sealed record Profile(string Name, double Inch);

internal static class SettingsStore
{
    private static string File => AppPaths.File("settings.json");

    public static AppSettings Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s));
        }
        catch { }
    }
}

internal static class StateStore
{
    private static string Dir => AppPaths.DataDir;
    private static string File => Path.Combine(Dir, "state.json");

    public static void Save(AppliedState s)
    {
        Directory.CreateDirectory(Dir);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AppliedState? Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<AppliedState>(System.IO.File.ReadAllText(File))
                : null;
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch { }
    }
}
