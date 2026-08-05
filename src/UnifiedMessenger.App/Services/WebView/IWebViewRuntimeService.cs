namespace UnifiedMessenger.App.Services.WebView;

public interface IWebViewRuntimeService
{
    Uri InstallerPageUri { get; }
    WebViewRuntimeInfo DetectRuntime();
}
