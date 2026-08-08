namespace UnifiedMessenger.App.Services.Notifications;

public interface IWebNotificationLifecycle
{
    bool WasShown { get; }
    bool IsCompleted { get; }
    void ReportShown();
    void ReportClicked();
    void ReportClosed();
    void CompleteSuppressed();
}
