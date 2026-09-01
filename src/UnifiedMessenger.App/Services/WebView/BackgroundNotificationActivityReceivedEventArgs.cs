using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class BackgroundNotificationActivityReceivedEventArgs(
    Guid serviceInstanceId,
    ServiceType serviceType) : EventArgs
{
    public Guid ServiceInstanceId { get; } = serviceInstanceId;
    public ServiceType ServiceType { get; } = serviceType;
}
