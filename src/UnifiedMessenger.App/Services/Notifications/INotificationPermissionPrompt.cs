namespace UnifiedMessenger.App.Services.Notifications;

public interface INotificationPermissionPrompt
{
    bool Show(string accountDisplayName);
}
