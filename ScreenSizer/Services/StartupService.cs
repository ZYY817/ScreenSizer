using Microsoft.Win32;

namespace ScreenSizer.Services;

/// <summary>开机自启（注册表 Run 键）。启动项名从旧名 ScreenSizer 迁移到 AimFrame。</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppPaths.AppName) != null || key?.GetValue(AppPaths.LegacyName) != null;
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        key.DeleteValue(AppPaths.LegacyName, false);
        if (enable) key.SetValue(AppPaths.AppName, $"\"{Environment.ProcessPath ?? ""}\"");
        else key.DeleteValue(AppPaths.AppName, false);
    }

    /// <summary>旧版写过 ScreenSizer 启动项 → 换成新名字和当前程序路径，避免开机启动两次。返回是否做了迁移。</summary>
    public static bool MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(AppPaths.LegacyName) == null) return false;
            key.DeleteValue(AppPaths.LegacyName, false);
            key.SetValue(AppPaths.AppName, $"\"{Environment.ProcessPath ?? ""}\"");
            return true;
        }
        catch { return false; }
    }
}
