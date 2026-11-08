using Microsoft.Win32;

namespace ScreenSizer.Services;

/// <summary>开机自启（注册表 Run 键）+ 应急还原。</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ScreenSizer";

    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (enable)
        {
            var exe = Environment.ProcessPath ?? "";
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else key.DeleteValue(AppName, false);
    }
}
