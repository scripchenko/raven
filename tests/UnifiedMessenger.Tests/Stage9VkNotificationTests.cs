using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class Stage9VkNotificationTests
{
    private const string MatchingEvent =
        "{\"backgroundServiceEvent\":{\"service\":\"notifications\",\"origin\":\"https://web.vk.me\",\"eventName\":\"Notification displayed\"}}";

    [Fact]
    public async Task NotificationsWithExactVkOrigin_RaiseOneActivitySignal()
    {
        FakeCdpClient client = new();
        int activitySignals = 0;
        using VkBackgroundNotificationMonitor? monitor =
            await VkBackgroundNotificationMonitor.TryStartAsync(client, () => activitySignals++);

        Assert.NotNull(monitor);
        client.Raise(MatchingEvent);

        Assert.Equal(1, activitySignals);
        Assert.Contains("clear", client.Operations);
    }

    [Theory]
    [InlineData("https://id.vk.com")]
    [InlineData("https://sub.web.vk.me")]
    [InlineData("https://web.vk.me/path")]
    [InlineData("http://web.vk.me")]
    public void NonExactOrigin_IsIgnored(string origin)
    {
        string json =
            $"{{\"backgroundServiceEvent\":{{\"service\":\"notifications\",\"origin\":\"{origin}\",\"eventName\":\"Notification displayed\"}}}}";

        Assert.False(VkBackgroundNotificationMonitor.IsMatchingEvent(json));
    }

    [Fact]
    public void WrongBackgroundService_IsIgnored()
    {
        Assert.False(VkBackgroundNotificationMonitor.IsMatchingEvent(
            "{\"backgroundServiceEvent\":{\"service\":\"pushMessaging\",\"origin\":\"https://web.vk.me\",\"eventName\":\"Notification displayed\"}}"));
    }

    [Fact]
    public void NotificationClosed_IsIgnored()
    {
        Assert.False(VkBackgroundNotificationMonitor.IsMatchingEvent(
            "{\"backgroundServiceEvent\":{\"service\":\"notifications\",\"origin\":\"https://web.vk.me\",\"eventName\":\"Notification closed\"}}"));
    }

    [Fact]
    public async Task DisplayedThenClosed_RaisesExactlyOneActivitySignal()
    {
        FakeCdpClient client = new();
        int activitySignals = 0;
        using VkBackgroundNotificationMonitor? monitor =
            await VkBackgroundNotificationMonitor.TryStartAsync(client, () => activitySignals++);

        client.Raise(MatchingEvent);
        client.Raise(
            "{\"backgroundServiceEvent\":{\"service\":\"notifications\",\"origin\":\"https://web.vk.me\",\"eventName\":\"Notification closed\"}}"
        );

        Assert.Equal(1, activitySignals);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"backgroundServiceEvent\":{\"service\":\"notifications\"}}")]
    [InlineData("{\"backgroundServiceEvent\":{\"origin\":\"https://web.vk.me\"}}")]
    [InlineData("{\"backgroundServiceEvent\":{\"service\":\"notifications\",\"origin\":\"https://web.vk.me\"}}")]
    [InlineData("{\"backgroundServiceEvent\":null}")]
    public void MalformedOrIncompleteEvent_IsIgnoredSafely(string json)
    {
        Assert.False(VkBackgroundNotificationMonitor.IsMatchingEvent(json));
    }

    [Fact]
    public async Task Startup_ClearsStaleEventsBeforeRecording()
    {
        FakeCdpClient client = new();
        using VkBackgroundNotificationMonitor? monitor =
            await VkBackgroundNotificationMonitor.TryStartAsync(client, static () => { });

        Assert.NotNull(monitor);
        Assert.Equal(
            ["subscribe", "record:false", "clear", "observe:start", "record:true"],
            client.Operations);
    }

    [Fact]
    public async Task Cleanup_DisablesRecordingStopsObservingAndClearsEvents()
    {
        FakeCdpClient client = new();
        int activitySignals = 0;
        VkBackgroundNotificationMonitor monitor =
            (await VkBackgroundNotificationMonitor.TryStartAsync(client, () => activitySignals++))!;

        await monitor.StopAsync();
        await monitor.StopAsync();
        client.Raise(MatchingEvent);

        Assert.Equal(
            [
                "subscribe", "record:false", "clear", "observe:start", "record:true",
                "unsubscribe", "record:false", "observe:stop", "clear", "dispose"
            ],
            client.Operations);
        Assert.Equal(0, activitySignals);
    }

    [Fact]
    public async Task UnsupportedCdp_DoesNotCrashOrReturnMonitor()
    {
        FakeCdpClient client = new() { ThrowOn = "observe:start" };

        VkBackgroundNotificationMonitor? monitor =
            await VkBackgroundNotificationMonitor.TryStartAsync(client, static () => { });

        Assert.Null(monitor);
        Assert.Contains("dispose", client.Operations);
    }

    [Fact]
    public async Task MultipleAcceptedEvents_DeliverEachCallbackWithoutPayloadStorage()
    {
        FakeCdpClient client = new();
        int activitySignals = 0;
        using VkBackgroundNotificationMonitor? monitor =
            await VkBackgroundNotificationMonitor.TryStartAsync(client, () => activitySignals++);

        client.Raise(MatchingEvent);
        client.Raise(MatchingEvent);

        Assert.Equal(2, activitySignals);
    }

    [Fact]
    public void NotificationDisplayed_DoesNotRequireEventMetadata()
    {
        Assert.True(VkBackgroundNotificationMonitor.IsMatchingEvent(MatchingEvent));
        Assert.DoesNotContain("eventMetadata", MatchingEvent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionMonitor_DoesNotStorePayloadOrCreateControllers()
    {
        DirectoryInfo? repositoryRoot = new(AppContext.BaseDirectory);
        while (repositoryRoot is not null
               && !File.Exists(Path.Combine(repositoryRoot.FullName, "UnifiedMessenger.sln")))
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        Assert.NotNull(repositoryRoot);
        string sourcePath = Path.Combine(
            repositoryRoot!.FullName,
            "src", "UnifiedMessenger.App", "Services", "WebView", "VkBackgroundNotificationMonitor.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("eventMetadata", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCoreWebView2Controller", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2Controller", source, StringComparison.Ordinal);
    }

    private sealed class FakeCdpClient : IVkBackgroundNotificationCdpClient
    {
        private Action<string>? _eventReceived;

        public List<string> Operations { get; } = [];
        public string? ThrowOn { get; init; }

        public event Action<string>? EventReceived
        {
            add
            {
                Operations.Add("subscribe");
                _eventReceived += value;
            }
            remove
            {
                Operations.Add("unsubscribe");
                _eventReceived -= value;
            }
        }

        public Task SetRecordingAsync(bool shouldRecord) =>
            RecordAsync($"record:{shouldRecord.ToString().ToLowerInvariant()}");

        public Task ClearEventsAsync() => RecordAsync("clear");

        public Task StartObservingAsync() => RecordAsync("observe:start");

        public Task StopObservingAsync() => RecordAsync("observe:stop");

        public void Dispose() => Operations.Add("dispose");

        public void Raise(string json) => _eventReceived?.Invoke(json);

        private Task RecordAsync(string operation) =>
            ThrowOn == operation
                ? Task.FromException(new InvalidOperationException("unsupported"))
                : RecordCompleted(operation);

        private Task RecordCompleted(string operation)
        {
            Operations.Add(operation);
            return Task.CompletedTask;
        }
    }

}
