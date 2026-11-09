using System.IO;

namespace ScreenSizer.Services;

/// <summary>
/// 用户数据目录：%LOCALAPPDATA%\AimFrame。
/// 首次运行时从旧目录 %LOCALAPPDATA%\ScreenSizer 复制数据过来（复制而非移动，旧版仍可用）。
/// 迁移失败不影响启动：退回继续使用旧目录。
/// </summary>
internal static class AppPaths
{
    public const string AppName = "AimFrame";
    public const string LegacyName = "ScreenSizer";

    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly Lazy<string> _dir = new(Resolve);

    public static string DataDir => _dir.Value;

    /// <summary>本次启动是否从旧目录迁移了数据（供日志使用）。</summary>
    public static bool Migrated { get; private set; }

    /// <summary>迁移出错信息；null = 无错误。</summary>
    public static string? MigrationError { get; private set; }

    public static string File(string name) => Path.Combine(DataDir, name);

    private static string Resolve()
    {
        string dir = Path.Combine(Root, AppName);
        string legacy = Path.Combine(Root, LegacyName);
        if (Directory.Exists(dir) || !Directory.Exists(legacy)) return dir;

        try
        {
            Directory.CreateDirectory(dir);
            // state.json 最重要：缩放生效中升级，靠它才能还原分辨率
            foreach (var name in new[] { "state.json", "settings.json", "app.log" })
            {
                var src = Path.Combine(legacy, name);
                if (System.IO.File.Exists(src)) System.IO.File.Copy(src, Path.Combine(dir, name), overwrite: false);
            }
            Migrated = true;
            return dir;
        }
        catch (Exception e)
        {
            MigrationError = e.Message;
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            return legacy;
        }
    }
}
