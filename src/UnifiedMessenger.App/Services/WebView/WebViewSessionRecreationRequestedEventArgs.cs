namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewSessionRecreationRequestedEventArgs(Guid serviceInstanceId) : EventArgs
{
    public Guid ServiceInstanceId { get; } = serviceInstanceId;
}
