using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Notifications;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebNotificationReceivedEventArgs(
    Guid serviceInstanceId,
    ServiceType serviceType,
    string senderOrigin,
    string title,
    string body,
    IWebNotificationLifecycle lifecycle) : EventArgs
{
    public Guid ServiceInstanceId { get; } = serviceInstanceId;
    public ServiceType ServiceType { get; } = serviceType;
    public string SenderOrigin { get; } = senderOrigin;
    public string Title { get; } = title;
    public string Body { get; } = body;
    public IWebNotificationLifecycle Lifecycle { get; } = lifecycle;
}
