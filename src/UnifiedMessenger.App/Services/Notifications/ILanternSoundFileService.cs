using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface ILanternSoundFileService
{
    string DefaultSoundPath { get; }

    Task<LanternSoundImportResult> ImportAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default);

    string ResolvePlaybackPath(NotificationSettings settings);
    void DeleteInternalCopy(string? internalFileName);
}

public sealed record LanternSoundImportResult(
    bool Success,
    string? InternalFileName = null,
    string? DisplayName = null,
    string? ErrorMessage = null)
{
    public static LanternSoundImportResult Failed(string message) =>
        new(false, ErrorMessage: message);
}
