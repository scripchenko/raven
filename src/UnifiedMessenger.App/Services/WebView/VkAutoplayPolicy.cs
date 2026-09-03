using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

internal static class VkAutoplayPolicy
{
    internal const string Origin = "https://web.vk.me";

    public static Task ApplyAsync(
        ServiceType serviceType,
        CoreWebView2Profile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ApplyAsync(serviceType, profile.SetPermissionStateAsync, cancellationToken);
    }

    internal static async Task ApplyAsync(
        ServiceType serviceType,
        Func<CoreWebView2PermissionKind, string, CoreWebView2PermissionState, Task> setPermissionStateAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setPermissionStateAsync);
        if (serviceType is not ServiceType.VkMessenger)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await setPermissionStateAsync(
            CoreWebView2PermissionKind.Autoplay,
            Origin,
            CoreWebView2PermissionState.Deny);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
