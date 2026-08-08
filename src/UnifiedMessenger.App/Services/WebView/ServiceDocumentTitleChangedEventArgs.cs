using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class ServiceDocumentTitleChangedEventArgs(
    Guid serviceInstanceId,
    ServiceType serviceType,
    string documentTitle) : EventArgs
{
    public Guid ServiceInstanceId { get; } = serviceInstanceId;
    public ServiceType ServiceType { get; } = serviceType;
    public string DocumentTitle { get; } = documentTitle;
}
