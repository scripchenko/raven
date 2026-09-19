using Microsoft.Web.WebView2.Core;

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
            ? "Сервис"
            : serviceDisplayName.Trim();

        return status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => "Не удалось найти адрес сервиса. Проверьте DNS и подключение к интернету.",
            CoreWebView2WebErrorStatus.Timeout => $"{serviceName} не ответил вовремя. Проверьте подключение и повторите попытку.",
            CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.CannotConnect =>
                $"Сервер {serviceName} сейчас недоступен. Проверьте интернет-соединение.",
            CoreWebView2WebErrorStatus.Disconnected
                or CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.ConnectionReset =>
                "Соединение было прервано. Проверьте интернет и повторите попытку.",
            _ => $"Не удалось загрузить {serviceName}. Повторите попытку."
        };
    }
}
