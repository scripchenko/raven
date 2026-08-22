namespace UnifiedMessenger.App.Services.WebView;

internal sealed class WebViewControllerCloseGuard
{
    private int _closed;

    public bool TryBeginClose() => Interlocked.Exchange(ref _closed, 1) == 0;
}
