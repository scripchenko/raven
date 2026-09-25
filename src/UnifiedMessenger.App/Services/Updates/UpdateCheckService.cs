using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Updates;

public sealed class UpdateCheckService : IUpdateCheckService
{
    private static readonly TimeSpan AutomaticThrottle = TimeSpan.FromHours(24);
    private readonly IRavenReleaseClient _client;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly TimeProvider _timeProvider;

    public UpdateCheckService(
        IRavenReleaseClient client,
        IApplicationSettingsStore settingsStore,
        TimeProvider timeProvider)
    {
        _client = client;
        _settingsStore = settingsStore;
        _timeProvider = timeProvider;
    }

    public Version CurrentVersion => RavenVersion.Current;

    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            AppSettings settings = _settingsStore.Current;
            if (!manual && settings.LastAutomaticUpdateCheckUtc is DateTimeOffset last
                && now - last < AutomaticThrottle)
            {
                return new(UpdateCheckStatus.Suppressed, CurrentVersion);
            }

            if (!manual)
            {
                settings.LastAutomaticUpdateCheckUtc = now;
                await _settingsStore.SaveAsync(cancellationToken);
            }

            RavenReleaseInfo? release = await _client.GetLatestStableReleaseAsync(cancellationToken);
            if (release is null)
            {
                return new(UpdateCheckStatus.NoRelease, CurrentVersion);
            }

            if (release.Version <= CurrentVersion)
            {
                return new(UpdateCheckStatus.Current, CurrentVersion, release);
            }

            if (!manual && string.Equals(settings.LastNotifiedUpdateVersion, release.Version.ToString(3), StringComparison.Ordinal))
            {
                return new(UpdateCheckStatus.Current, CurrentVersion, release);
            }

            if (!manual)
            {
                settings.LastNotifiedUpdateVersion = release.Version.ToString(3);
                await _settingsStore.SaveAsync(cancellationToken);
            }

            return new(UpdateCheckStatus.UpdateAvailable, CurrentVersion, release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(UpdateCheckStatus.Failed, CurrentVersion);
        }
    }
}
