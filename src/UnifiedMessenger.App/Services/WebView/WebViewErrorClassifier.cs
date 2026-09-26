using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Services.Localization;

namespace UnifiedMessenger.App.Services.WebView;

public static class WebViewErrorClassifier
{
    public static bool IsConnectivityFailure(CoreWebView2WebErrorStatus status) =>
        status is CoreWebView2WebErrorStatus.ServerUnreachable
            or CoreWebView2WebErrorStatus.Timeout
            or CoreWebView2WebErrorStatus.ConnectionAborted
            or CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.Disconnected
            or CoreWebView2WebErrorStatus.CannotConnect
            or CoreWebView2WebErrorStatus.HostNameNotResolved;

    public static string GetUserMessage(
        CoreWebView2WebErrorStatus status,
        string serviceDisplayName)
    {
        string serviceName = string.IsNullOrWhiteSpace(serviceDisplayName)
            ? Localizer.Instance.Get("Service")
            : serviceDisplayName.Trim();

        return status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => Localizer.Instance.Get("Could not find the service address. Check DNS and your connection."),
            CoreWebView2WebErrorStatus.Timeout => Localizer.Instance.Format("{0} did not respond in time. Check your connection and retry.", serviceName),
            CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.CannotConnect =>
                Localizer.Instance.Format("The {0} server is unavailable. Check your Internet connection.", serviceName),
            CoreWebView2WebErrorStatus.Disconnected
                or CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.ConnectionReset =>
                Localizer.Instance.Get("The connection was interrupted. Check the Internet and retry."),
            _ => Localizer.Instance.Format("Could not load {0}. Retry.", serviceName)
        };
    }
}
