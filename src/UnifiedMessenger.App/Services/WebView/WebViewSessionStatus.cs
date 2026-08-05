namespace UnifiedMessenger.App.Services.WebView;

public enum WebViewSessionStatus
{
    Uninitialized,
    Initializing,
    Ready,
    Navigating,
    Offline,
    Failed
}
