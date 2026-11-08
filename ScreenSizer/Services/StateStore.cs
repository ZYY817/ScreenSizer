using System.IO;
using System.Text.Json;

namespace ScreenSizer.Services;

public sealed class AppliedState
{
    public required string DeviceName { get; init; }
    public uint DisplayId { get; init; }
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
}

internal static class SettingsStore
{
    private static string File => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSizer", "settings.json");

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
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSizer");
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
