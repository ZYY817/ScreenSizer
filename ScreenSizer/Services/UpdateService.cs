using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ScreenSizer.Services;

/// <summary>
/// 通过 GitHub Releases 检查更新。
/// 主路径：请求 /releases/latest 网页，读 302 跳转里的 tag —— 不走 API，不受匿名 60 次/小时限制。
/// 更新说明：再尝试一次 API 取 body，失败不影响结果。
/// </summary>
internal static class UpdateService
{
    public const string Repo = "ZYY817/ScreenSizer";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";

    public enum Status { Latest, Available, Error }

    public sealed record Result(Status Status, string? Tag = null, string? Url = null, string[]? Notes = null, DateTime? Date = null, string? Error = null);

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    public static string CurrentText => $"v{Current.Major}.{Current.Minor}.{Current.Build}";

    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AimFrame/{CurrentText.TrimStart('v')}");

            using var resp = await http.GetAsync(RepoUrl + "/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
            var loc = resp.Headers.Location?.ToString() ?? "";
            int i = loc.IndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
            if (resp.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect) || i < 0)
                return new Result(Status.Latest); // 还没有发布任何 Release

            string tag = Uri.UnescapeDataString(loc[(i + 5)..].Trim('/'));
            if (!TryParse(tag, out var latest)) return new Result(Status.Error, Error: "无法解析版本号 " + tag);
            if (latest <= Current) return new Result(Status.Latest, tag);

            string[]? notes = null; DateTime? date = null;
            try
            {
                using var api = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                api.DefaultRequestHeaders.UserAgent.ParseAdd($"AimFrame/{CurrentText.TrimStart('v')}");
                var json = await api.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("body", out var b) && b.GetString() is { } body)
                    notes = body.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '+', ' ').Trim())
                                .Where(l => l.Length > 0 && !l.StartsWith('#')).Take(4).ToArray();
                if (doc.RootElement.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt)) date = dt.ToLocalTime();
            }
            catch { /* 说明拿不到不影响提示 */ }

            return new Result(Status.Available, tag, $"{RepoUrl}/releases/tag/{tag}", notes, date);
        }
        catch (Exception e) { return new Result(Status.Error, Error: $"{e.GetType().Name}: {e.InnerException?.Message ?? e.Message}"); }
    }

    private static bool TryParse(string tag, out Version v)
    {
        var s = tag.TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) s = s[..dash];
        if (Version.TryParse(s, out var p)) { v = new Version(p.Major, p.Minor, Math.Max(0, p.Build)); return true; }
        v = new Version(0, 0); return false;
    }
}
