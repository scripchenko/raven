using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace UnifiedMessenger.App.Services.WebView;

internal interface IVkBackgroundNotificationCdpClient : IDisposable
{
    event Action<string>? EventReceived;

    Task SetRecordingAsync(bool shouldRecord);
    Task ClearEventsAsync();
    Task StartObservingAsync();
    Task StopObservingAsync();
}

internal sealed class VkBackgroundNotificationMonitor : IDisposable
{
    internal const string OfficialOrigin = "https://web.vk.me";
    private readonly IVkBackgroundNotificationCdpClient _client;
    private readonly Action _activityReceived;
    private readonly object _stopSync = new();
    private Task? _stopTask;
    private int _stopStarted;
    private bool _startupAttempted;

    private VkBackgroundNotificationMonitor(
        IVkBackgroundNotificationCdpClient client,
        Action activityReceived)
    {
        _client = client;
        _activityReceived = activityReceived;
    }

    public static async Task<VkBackgroundNotificationMonitor?> TryStartAsync(
        CoreWebView2 coreWebView,
        Action activityReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        ArgumentNullException.ThrowIfNull(activityReceived);

        try
        {
            return await TryStartAsync(
                new CoreWebView2VkBackgroundNotificationCdpClient(coreWebView),
                activityReceived,
                cancellationToken);
        }
        catch (Exception exception) when (IsOptionalProtocolFailure(exception))
        {
            return null;
        }
    }

    internal static async Task<VkBackgroundNotificationMonitor?> TryStartAsync(
        IVkBackgroundNotificationCdpClient client,
        Action activityReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(activityReceived);

        VkBackgroundNotificationMonitor monitor = new(client, activityReceived);
        try
        {
            await monitor.StartAsync(cancellationToken);
            return monitor;
        }
        catch (Exception exception) when (IsOptionalProtocolFailure(exception))
        {
            await monitor.StopAsync();
            return null;
        }
        catch
        {
            await monitor.StopAsync();
            throw;
        }
    }

    internal static bool IsMatchingEvent(string parameterObjectJson)
    {
        if (string.IsNullOrWhiteSpace(parameterObjectJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(parameterObjectJson);
            if (!document.RootElement.TryGetProperty("backgroundServiceEvent", out JsonElement backgroundEvent)
                || backgroundEvent.ValueKind is not JsonValueKind.Object
                || !backgroundEvent.TryGetProperty("service", out JsonElement serviceElement)
                || serviceElement.ValueKind is not JsonValueKind.String
                || !string.Equals(serviceElement.GetString(), "notifications", StringComparison.Ordinal)
                || !backgroundEvent.TryGetProperty("origin", out JsonElement originElement)
                || originElement.ValueKind is not JsonValueKind.String
                || !backgroundEvent.TryGetProperty("eventName", out JsonElement eventNameElement)
                || eventNameElement.ValueKind is not JsonValueKind.String
                || !string.Equals(
                    eventNameElement.GetString(),
                    "Notification displayed",
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsExactOfficialOrigin(originElement.GetString()))
            {
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsExactOfficialOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? origin)
        && origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && origin.Host.Equals("web.vk.me", StringComparison.OrdinalIgnoreCase)
        && origin.IsDefaultPort
        && string.IsNullOrEmpty(origin.UserInfo)
        && origin.AbsolutePath == "/"
        && string.IsNullOrEmpty(origin.Query)
        && string.IsNullOrEmpty(origin.Fragment);

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.EventReceived += OnEventReceived;
        _startupAttempted = true;
        await _client.SetRecordingAsync(shouldRecord: false).WaitAsync(cancellationToken);
        await _client.ClearEventsAsync().WaitAsync(cancellationToken);
        await _client.StartObservingAsync().WaitAsync(cancellationToken);
        await _client.SetRecordingAsync(shouldRecord: true).WaitAsync(cancellationToken);
    }

    private void OnEventReceived(string parameterObjectJson)
    {
        if (Volatile.Read(ref _stopStarted) != 0
            || !IsMatchingEvent(parameterObjectJson))
        {
            return;
        }

        _activityReceived();
        _ = ClearDeliveredEventsAsync();
    }

    private async Task ClearDeliveredEventsAsync()
    {
        try
        {
            await _client.ClearEventsAsync();
        }
        catch (Exception exception) when (IsOptionalProtocolFailure(exception))
        {
            // The activity callback has already been delivered. Event storage cleanup is best effort.
        }
    }

    internal Task StopAsync()
    {
        lock (_stopSync)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            Interlocked.Exchange(ref _stopStarted, 1);
            return _stopTask = StopCoreAsync();
        }
    }

    public void Dispose() => _ = StopAsync();

    private async Task StopCoreAsync()
    {
        _client.EventReceived -= OnEventReceived;
        if (_startupAttempted)
        {
            await TryCleanupCommandAsync(() => _client.SetRecordingAsync(shouldRecord: false));
            await TryCleanupCommandAsync(_client.StopObservingAsync);
            await TryCleanupCommandAsync(_client.ClearEventsAsync);
        }
        _client.Dispose();
    }

    private static async Task TryCleanupCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception exception) when (IsOptionalProtocolFailure(exception))
        {
            // A closing/crashed WebView2 may no longer accept experimental protocol commands.
        }
    }

    private static bool IsOptionalProtocolFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or NotImplementedException
            or ObjectDisposedException
            or COMException;
}

internal sealed class CoreWebView2VkBackgroundNotificationCdpClient : IVkBackgroundNotificationCdpClient
{
    private const string NotificationsParameter = "{\"service\":\"notifications\"}";
    private readonly CoreWebView2 _coreWebView;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _receiver;

    public CoreWebView2VkBackgroundNotificationCdpClient(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        _receiver = coreWebView.GetDevToolsProtocolEventReceiver(
            "BackgroundService.backgroundServiceEventReceived");
        _receiver.DevToolsProtocolEventReceived += OnDevToolsProtocolEventReceived;
    }

    public event Action<string>? EventReceived;

    public Task SetRecordingAsync(bool shouldRecord) =>
        _coreWebView.CallDevToolsProtocolMethodAsync(
            "BackgroundService.setRecording",
            shouldRecord
                ? "{\"service\":\"notifications\",\"shouldRecord\":true}"
                : "{\"service\":\"notifications\",\"shouldRecord\":false}");

    public Task ClearEventsAsync() =>
        _coreWebView.CallDevToolsProtocolMethodAsync(
            "BackgroundService.clearEvents",
            NotificationsParameter);

    public Task StartObservingAsync() =>
        _coreWebView.CallDevToolsProtocolMethodAsync(
            "BackgroundService.startObserving",
            NotificationsParameter);

    public Task StopObservingAsync() =>
        _coreWebView.CallDevToolsProtocolMethodAsync(
            "BackgroundService.stopObserving",
            NotificationsParameter);

    public void Dispose() =>
        _receiver.DevToolsProtocolEventReceived -= OnDevToolsProtocolEventReceived;

    private void OnDevToolsProtocolEventReceived(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs eventArgs) =>
        EventReceived?.Invoke(eventArgs.ParameterObjectAsJson);
}
