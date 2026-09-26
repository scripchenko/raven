using System.Xml.Linq;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Updates;
using UnifiedMessenger.App.Services;

namespace UnifiedMessenger.Tests;

public sealed class RavenUpdateTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    public void StableVersionComparisonUsesSemanticVersion(string candidateText, string currentText, bool newer)
    {
        Assert.True(RavenVersion.TryParse(candidateText, out Version? candidate));
        Assert.True(RavenVersion.TryParse(currentText, out Version? current));
        Assert.Equal(newer, candidate! > current!);
    }

    [Theory]
    [InlineData("v0.1.1-beta", false)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void StableChannelRejectsMalformedAndPrereleaseTags(string tag, bool expected)
    {
        Assert.Equal(expected, RavenVersion.TryParse(tag, out _));
    }

    [Fact]
    public void PublicReleaseEndpointAndSupportLinkAreStableHttpsUrls()
    {
        Assert.Equal(
            "https://api.github.com/repos/scripchenko/raven/releases/latest",
            GitHubReleaseClient.LatestReleaseEndpoint);
        Assert.Equal(
            "https://github.com/scripchenko/raven/releases",
            GitHubReleaseClient.ReleasesPage);
        Assert.Equal("https://t.me/dscripchenko", "https://t.me/dscripchenko");
    }

    [Fact]
    public void WelcomeAndAboutExposeExpectedUserFacingText()
    {
        string welcome = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "WelcomeView.xaml"));
        string welcomeCode = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "WelcomeView.xaml.cs"));
        string about = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "SettingsView.xaml"));
        Assert.Contains("{loc:Text Key='Welcome to raven'}", welcome, StringComparison.Ordinal);
        Assert.Contains("{loc:Text Key='Messaging and email in one place.'}", welcome, StringComparison.Ordinal);
        Assert.Contains("{loc:Text Key='Add your accounts in Settings to get started.'}", welcome, StringComparison.Ordinal);
        Assert.Contains("{loc:Text Key='Need help?'}", welcome, StringComparison.Ordinal);
        Assert.Contains("NavigateUri=\"https://t.me/dscripchenko\"", welcome, StringComparison.Ordinal);
        Assert.Contains("TryOpen(eventArgs.Uri)", welcomeCode, StringComparison.Ordinal);
        Assert.DoesNotContain("<Button", welcome, StringComparison.Ordinal);
        Assert.Contains("{loc:Text Key='Check for updates'}", about, StringComparison.Ordinal);
        Assert.Contains("OpenSupportCommand", about, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticCheckIsThrottledButManualCheckBypassesIt()
    {
        FakeSettingsStore store = new();
        FakeReleaseClient client = new(new RavenReleaseInfo(new Version(0, 1, 1), new Uri(GitHubReleaseClient.ReleasesPage)));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        UpdateCheckService service = new(client, store, time);

        UpdateCheckResult first = await service.CheckAsync(manual: false);
        UpdateCheckResult suppressed = await service.CheckAsync(manual: false);
        UpdateCheckResult manual = await service.CheckAsync(manual: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, first.Status);
        Assert.Equal(UpdateCheckStatus.Suppressed, suppressed.Status);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, manual.Status);
        Assert.Equal(2, client.RequestCount);
    }

    [Fact]
    public async Task AutomaticCheckDoesNotRepeatTheSameAnnouncement()
    {
        FakeSettingsStore store = new();
        FakeReleaseClient client = new(new RavenReleaseInfo(new Version(0, 1, 1), new Uri(GitHubReleaseClient.ReleasesPage)));
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        UpdateCheckService service = new(client, store, time);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await service.CheckAsync(false)).Status);
        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(UpdateCheckStatus.Current, (await service.CheckAsync(false)).Status);
        client.Release = new RavenReleaseInfo(new Version(0, 1, 2), new Uri(GitHubReleaseClient.ReleasesPage));
        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await service.CheckAsync(false)).Status);
    }

    [Fact]
    public async Task AutomaticPersistenceAccessDeniedIsNonFatalAndNeverContactsNetwork()
    {
        FakeSettingsStore store = new() { SaveException = new UnauthorizedAccessException("denied") };
        FakeReleaseClient client = new(new RavenReleaseInfo(new Version(0, 1, 1), new Uri(GitHubReleaseClient.ReleasesPage)));
        UpdateCheckService service = new(client, store, new FakeTimeProvider(DateTimeOffset.UtcNow));

        UpdateCheckResult result = await service.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(0, client.RequestCount);
    }

    [Fact]
    public void StartupDiagnosticsUsesEstablishedWritableLocalAppDataRoot()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnifiedMessenger",
            "Diagnostics");
        Assert.Equal(
            Path.Combine(expectedRoot, "startup-error.log"),
            StartupDiagnostics.LogPath);
        Assert.False(StartupDiagnostics.LogPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        string directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine([directory, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class FakeReleaseClient(RavenReleaseInfo? release) : IRavenReleaseClient
    {
        public RavenReleaseInfo? Release { get; set; } = release;
        public int RequestCount { get; private set; }
        public Task<RavenReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(Release);
        }
    }

    private sealed class FakeSettingsStore : IApplicationSettingsStore
    {
        public AppSettings Current { get; } = AppSettings.CreateDefault();
        public bool IsInitialized => true;
        public int SaveCount { get; private set; }
        public Exception? SaveException { get; init; }
        public void Initialize(AppSettings settings) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;
        public override DateTimeOffset GetUtcNow() => _current;
        public void Advance(TimeSpan amount) => _current += amount;
    }
}
