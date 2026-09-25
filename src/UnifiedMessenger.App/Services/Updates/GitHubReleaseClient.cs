using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace UnifiedMessenger.App.Services.Updates;

public sealed class GitHubReleaseClient : IRavenReleaseClient
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/scripchenko/Lantern/releases/latest";
    public const string ReleasesPage = "https://github.com/scripchenko/Lantern/releases";
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("raven", "0.1.0"));
        }
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<RavenReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(LatestReleaseEndpoint, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()
            || root.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean())
        {
            return null;
        }

        string? tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString()
            : null;
        if (!RavenVersion.TryParse(tag, out Version? version))
        {
            return null;
        }

        string? htmlUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
            ? urlElement.GetString()
            : null;
        Uri releaseUrl = Uri.TryCreate(htmlUrl, UriKind.Absolute, out Uri? parsed)
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? parsed
            : new Uri(ReleasesPage);
        return new RavenReleaseInfo(version!, releaseUrl);
    }
}

public static class RavenVersion
{
    public static Version Current => GetCurrent();

    public static bool TryParse(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        if (normalized.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3 || parts.Any(part => !int.TryParse(part, out int number) || number < 0))
        {
            return false;
        }

        version = Version.Parse(string.Join('.', parts));
        return true;
    }

    private static Version GetCurrent()
    {
        Version? version = typeof(RavenVersion).Assembly.GetName().Version;
        return version is null ? new Version(0, 1, 0) : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }
}
