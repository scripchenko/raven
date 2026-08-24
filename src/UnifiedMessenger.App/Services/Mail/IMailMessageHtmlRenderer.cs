using System.Drawing;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public interface IMailMessageHtmlRenderer : IDisposable
{
    bool IsInitialized { get; }
    bool IsVisible { get; }
    int ControllerCount { get; }

    Task ShowAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        MailMessageContent content,
        IReadOnlyDictionary<string, MailImageContent>? remoteImages,
        bool isVisible,
        CancellationToken cancellationToken = default);

    void UpdateLayout(Rectangle bounds, bool isVisible);
    void NotifyParentWindowPositionChanged();
    void Hide(bool clearContent);
    void ReleaseController(bool clearContent);
    void BeginShutdown();
}

public sealed class MailRendererWindowLifecycleCoordinator(IMailMessageHtmlRenderer renderer)
{
    private Rectangle _lastAppliedBounds;
    private bool _hasLastAppliedBounds;
    private bool _isComposeActive;

    public void UpdateSurface(bool shouldShow, Func<Rectangle?> boundsProvider)
    {
        ArgumentNullException.ThrowIfNull(boundsProvider);
        if (_isComposeActive || !renderer.IsInitialized)
        {
            return;
        }

        if (!shouldShow)
        {
            if (renderer.IsVisible)
            {
                renderer.Hide(clearContent: false);
            }

            _hasLastAppliedBounds = false;
            return;
        }

        Rectangle? bounds = boundsProvider();
        if (bounds is Rectangle currentBounds
            && (!renderer.IsVisible || !_hasLastAppliedBounds || _lastAppliedBounds != currentBounds))
        {
            renderer.UpdateLayout(currentBounds, isVisible: true);
            _lastAppliedBounds = currentBounds;
            _hasLastAppliedBounds = true;
        }
    }

    public void SetComposeActive(bool isActive)
    {
        if (_isComposeActive == isActive)
        {
            return;
        }

        _isComposeActive = isActive;
        if (isActive)
        {
            // Closing the direct controller removes its native child HWND from the WPF input surface.
            // This is safe before initialization completes and invalidates an in-flight creation.
            renderer.ReleaseController(clearContent: false);
            _hasLastAppliedBounds = false;
        }
    }

    public void NotifyParentWindowPositionChanged()
    {
        if (renderer.IsVisible)
        {
            renderer.NotifyParentWindowPositionChanged();
        }
    }

    public void Deactivate(bool clearContent)
    {
        if (renderer.IsInitialized)
        {
            renderer.ReleaseController(clearContent);
        }

        _hasLastAppliedBounds = false;
    }

    public bool ReleaseForInteractiveMove()
    {
        if (!renderer.IsVisible)
        {
            return false;
        }

        renderer.ReleaseController(clearContent: false);
        _hasLastAppliedBounds = false;
        return true;
    }
}

public static class MailRendererVisibilityPolicy
{
    public static bool ShouldShow(
        bool windowVisible,
        bool windowMinimized,
        bool settingsOpen,
        bool hasSelectedWebService,
        bool hasActiveMailAccount,
        MailMessageBodyKind? bodyKind) =>
        windowVisible
        && !windowMinimized
        && !settingsOpen
        && !hasSelectedWebService
        && hasActiveMailAccount
        && bodyKind is MailMessageBodyKind.SanitizedHtml;
}
