namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewSessionStateChangedEventArgs(
    Guid? serviceInstanceId,
    WebViewSessionState state) : EventArgs
{
    public Guid? ServiceInstanceId { get; } = serviceInstanceId;
    public WebViewSessionState State { get; } = state;
}
