using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.Services.Mail;

public enum MailRendererNavigationDisposition
{
    InternalDocument,
    ExternalOpened,
    Blocked
}

public sealed class MailRendererNavigationPolicy
{
    public static readonly Uri InternalDocumentUri = new("https://mail-renderer.invalid/message");

    public MailRendererNavigationDisposition ClassifyTopLevel(Uri target, bool isUserInitiated)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (IsInternalDocument(target))
        {
            return MailRendererNavigationDisposition.InternalDocument;
        }

        return isUserInitiated
            && target.IsAbsoluteUri
            && (string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                ? MailRendererNavigationDisposition.ExternalOpened
                : MailRendererNavigationDisposition.Blocked;
    }

    public bool IsInternalDocument(Uri target) =>
        target.IsAbsoluteUri
        && string.Equals(
            target.GetLeftPart(UriPartial.Path),
            InternalDocumentUri.GetLeftPart(UriPartial.Path),
            StringComparison.OrdinalIgnoreCase);

    public bool IsAllowedInRendererResource(Uri target, CoreWebView2WebResourceContext resourceContext) =>
        resourceContext is CoreWebView2WebResourceContext.Document && IsInternalDocument(target)
        || resourceContext is CoreWebView2WebResourceContext.Image && IsSupportedMemoryImage(target);

    public bool IsSupportedMemoryImage(Uri target) =>
        target.IsAbsoluteUri
        && string.Equals(target.Scheme, "data", StringComparison.OrdinalIgnoreCase)
        && (target.OriginalString.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)
            || target.OriginalString.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)
            || target.OriginalString.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase)
            || target.OriginalString.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase));
}

public sealed class MailRendererNavigationCoordinator(
    MailRendererNavigationPolicy policy,
    IExternalBrowserService externalBrowserService)
{
    public MailRendererNavigationDisposition RouteTopLevel(Uri target, bool isUserInitiated)
    {
        MailRendererNavigationDisposition disposition = policy.ClassifyTopLevel(target, isUserInitiated);
        if (disposition is not MailRendererNavigationDisposition.ExternalOpened)
        {
            return disposition;
        }

        return externalBrowserService.TryOpen(target)
            ? MailRendererNavigationDisposition.ExternalOpened
            : MailRendererNavigationDisposition.Blocked;
    }
}
