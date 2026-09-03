using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebNewWindowNavigationService(WebNavigationService navigationService)
{
    public WebNavigationDisposition Route(
        ServiceInstance serviceInstance,
        Uri target,
        bool isUserInitiated,
        Action<Uri> navigateInCurrentWebView)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(navigateInCurrentWebView);

        WebNavigationDisposition disposition = navigationService.Route(
            serviceInstance.ServiceType,
            target,
            isUserInitiated);
        if (disposition is WebNavigationDisposition.Internal)
        {
            navigateInCurrentWebView(target);
        }

        return disposition;
    }
}
