using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TuneUp.Services;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string Notes);

/// <summary>
/// Asks GitHub whether a newer release exists (one small request when Vanta starts and then once a
/// day, only when the setting is on). Nothing about the PC is sent. Vanta never downloads or runs anything by itself:
/// it only shows a link to the release page.
/// </summary>
public static class UpdateService
{
    /// <summary>"owner/repo", baked in by the release build. Empty in local builds (check disabled).</summary>
    public static string Repo { get; } =
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value ?? "";

    public static bool Available => Repo.Contains('/');

    public static Version Current { get; } =
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    public static string ReleasesPage => Available ? $"https://github.com/{Repo}/releases" : "";

    /// <summary>The newer release, or null when up to date / offline / not configured.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!Available) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Vanta/{Current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;
            return new UpdateInfo(latest, tag,
                root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }
        catch (Exception ex)
        {
            AppLog.Info("Update check skipped: " + ex.Message);
            return null;
        }
    }
}
