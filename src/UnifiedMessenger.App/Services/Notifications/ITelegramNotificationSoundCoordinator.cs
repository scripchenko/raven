namespace UnifiedMessenger.App.Services.Notifications;

public interface ITelegramNotificationSoundCoordinator : IDisposable
{
    bool IsShutdownStarted { get; }
    bool RequestSound(TelegramNotificationSoundRequest request);
    void Shutdown();
}
