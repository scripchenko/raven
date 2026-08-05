namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewSessionStateChangedEventArgs(WebViewSessionState state) : EventArgs
{
    public WebViewSessionState State { get; } = state;
}
