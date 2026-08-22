using System.Drawing;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public interface IWebViewSessionManager : IDisposable
{
    event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged;
    event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested;
    event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged;
    event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived;

    WebViewSessionState State { get; }
    bool IsShutdownStarted { get; }
    int InitializedSessionCount { get; }
    int InitialNavigationCount { get; }
    Task<bool> InitializeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        ServiceInstance serviceInstance,
        bool activate,
        CancellationToken cancellationToken = default);
    Task<bool> PrimeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default);
    bool IsSessionInitialized(Guid serviceInstanceId);
    void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false);
    void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible);
    void NotifyParentWindowPositionChanged();
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
