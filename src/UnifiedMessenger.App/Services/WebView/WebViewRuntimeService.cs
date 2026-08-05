using System.IO;
using Microsoft.Web.WebView2.Core;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewRuntimeService : IWebViewRuntimeService
{
    public Uri InstallerPageUri { get; } = new("https://developer.microsoft.com/en-us/microsoft-edge/webview2/");

    public WebViewRuntimeInfo DetectRuntime()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version)
                ? WebViewRuntimeInfo.Missing
                : new WebViewRuntimeInfo(true, version);
        }
        catch (Exception exception) when (
            exception is WebView2RuntimeNotFoundException
                or FileNotFoundException
                or DllNotFoundException)
        {
            return WebViewRuntimeInfo.Missing;
        }
    }
}
