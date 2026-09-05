using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wildlands.Toolkit;

public sealed record ToolkitUpdate(Version LocalVersion, Version Version, string Tag,
    string Name, string Notes, Uri ReleasePage)
{
    public bool IsAvailable => Version.CompareTo(LocalVersion) > 0;
}

public static class UpdateChecker
{
    const string LatestReleaseApi =
        "https://api.github.com/repos/AlphaGlyph1371/WildlandsToolkit/releases/latest";

    static readonly HttpClient Client = CreateClient();

    public static async Task<ToolkitUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream,
            cancellationToken: cancellationToken);
        if (release is null
            || !TryParseVersion(release.Tag, out Version online)
            || !TryGetReleaseUri(release.Page, out Uri page))
            return null;

        Version local = CurrentVersion();
        string name = string.IsNullOrWhiteSpace(release.Name) ? release.Tag : release.Name.Trim();
        return new ToolkitUpdate(local, online, release.Tag.Trim(), name,
            release.Notes?.Trim() ?? "", page);
    }

    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        int suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            text = text[..suffix];

        string[] parts = text.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
            return false;

        int[] numbers = new int[4];
        for (int index = 0; index < parts.Length; index++)
            if (parts[index].Length == 0
                || !int.TryParse(parts[index], out numbers[index])
                || numbers[index] < 0)
                return false;

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));

    static Version CurrentVersion() => Normalize(Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0, 0));

    static bool TryGetReleaseUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && parsed.AbsolutePath.StartsWith("/AlphaGlyph1371/WildlandsToolkit/releases/",
                StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WildlandsToolkit-UpdateCheck");
        return client;
    }

    sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string Tag { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("body")]
        public string? Notes { get; init; }

        [JsonPropertyName("html_url")]
        public string Page { get; init; } = "";
    }
}
