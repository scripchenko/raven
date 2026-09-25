namespace UnifiedMessenger.App.Services.Updates;

public sealed record RavenReleaseInfo(Version Version, Uri ReleaseUrl);

public enum UpdateCheckStatus
{
    Current,
    UpdateAvailable,
    NoRelease,
    Suppressed,
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version CurrentVersion, RavenReleaseInfo? Release = null);

public interface IRavenReleaseClient
{
    Task<RavenReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default);
}

public interface IUpdateCheckService
{
    Version CurrentVersion { get; }
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);
}
