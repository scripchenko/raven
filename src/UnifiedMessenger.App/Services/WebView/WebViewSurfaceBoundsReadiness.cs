using System.Drawing;

namespace UnifiedMessenger.App.Services.WebView;

internal static class WebViewSurfaceBoundsReadiness
{
    public static async Task<Rectangle> GetReadyBoundsAsync(
        Func<bool> hasPositiveLayout,
        Func<CancellationToken, Task> waitForLayoutAsync,
        Func<Rectangle> getBounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hasPositiveLayout);
        ArgumentNullException.ThrowIfNull(waitForLayoutAsync);
        ArgumentNullException.ThrowIfNull(getBounds);

        if (!hasPositiveLayout())
        {
            await waitForLayoutAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Rectangle bounds = getBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("The WebView2 surface did not receive a positive layout.");
        }

        return bounds;
    }
}
