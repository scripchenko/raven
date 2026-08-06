using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebNavigationService(
    NavigationPolicy navigationPolicy,
    IExternalBrowserService externalBrowserService)
{
    public WebNavigationDisposition Classify(ServiceType serviceType, Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (navigationPolicy.IsAllowedTopLevelNavigation(serviceType, target))
        {
            return WebNavigationDisposition.Internal;
        }

        return WebNavigationDisposition.External;
    }

    public WebNavigationDisposition OpenExternal(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return externalBrowserService.TryOpen(target)
            ? WebNavigationDisposition.ExternalOpened
            : WebNavigationDisposition.Blocked;
    }

    public WebNavigationDisposition Route(ServiceType serviceType, Uri target)
    {
        WebNavigationDisposition disposition = Classify(serviceType, target);
        return disposition is WebNavigationDisposition.Internal
            ? disposition
            : OpenExternal(target);
    }
}
