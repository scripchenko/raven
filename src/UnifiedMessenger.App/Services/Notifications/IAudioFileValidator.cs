namespace UnifiedMessenger.App.Services.Notifications;

public interface IAudioFileValidator
{
    Task<bool> CanOpenAsync(string filePath, CancellationToken cancellationToken = default);
}
