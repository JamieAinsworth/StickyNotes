using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace StickyNotes;

/// <summary>Result of an update check. <see cref="Latest"/> is null when no release has been published.</summary>
public sealed record UpdateInfo(Version Current, Version? Latest, string Url)
{
    public bool IsNewer => Latest != null && Latest > Current;
}

/// <summary>Looks up the newest published GitHub release and compares it with the running version.</summary>
public static class UpdateChecker
{
    private const string Repository = "JamieAinsworth/StickyNotes";

    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StickyNotes-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateInfo(CurrentVersion, null, ReleasesUrl);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
        return new UpdateInfo(CurrentVersion, ParseTag(tag), SafeReleaseUrl(url));
    }

    /// <summary>The release page to open: only an https github.com address is trusted, anything else falls back.</summary>
    public static string SafeReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : ReleasesUrl;

    /// <summary>Reads "v1.2.3" (optionally with a "-beta" or "+build" suffix) as a version.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim().TrimStart('v', 'V');
        int suffix = text.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0) text = text[..suffix];

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    // 1.2.3 and 1.2.3.0 are the same release, but System.Version orders them differently.
    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
}
