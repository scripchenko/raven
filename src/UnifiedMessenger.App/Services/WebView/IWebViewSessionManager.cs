using UnifiedMessenger.App.Models;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace UnifiedMessenger.App.Services.WebView;

public interface IWebViewSessionManager : IDisposable
{
    event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged;
    event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested;
    event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged;
    event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived;

    WebViewSessionState State { get; }
    bool IsShutdownStarted { get; }
    WpfWebView2 CreateWebView(ServiceInstance serviceInstance);
    Task<bool> InitializeAsync(
        WpfWebView2 webView,
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default);
    bool HasSession(Guid serviceInstanceId);
    void DeactivateSession();
    void GoBack();
    void GoForward();
    void Reload();
    void NavigateHome();
    void Retry();
    void ReleaseSession(Guid serviceInstanceId);
    Task<bool> ClearProfileAsync(
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default);
    void ReleaseAllSessions();
    void BeginShutdown();
}
