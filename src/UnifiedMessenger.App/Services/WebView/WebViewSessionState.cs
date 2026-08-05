namespace UnifiedMessenger.App.Services.WebView;

public sealed record WebViewSessionState(
    WebViewSessionStatus Status,
    bool CanGoBack,
    bool CanGoForward,
    string? ErrorTitle = null,
    string? ErrorMessage = null,
    string? ErrorCode = null)
{
    public bool IsLoading => Status is WebViewSessionStatus.Initializing or WebViewSessionStatus.Navigating;

    public static WebViewSessionState Uninitialized { get; } =
        new(WebViewSessionStatus.Uninitialized, false, false);
}
