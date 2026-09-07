using System.Drawing;
using System.IO;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class WebViewSessionSettlementTests
{
    private static readonly Rectangle Bounds = new(0, 0, 800, 600);

    [Fact]
    public async Task ReleaseBeforeInitializationEntersGate_DoesNotStartReleasedSession()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment firstEnvironment = environments.Enqueue();
        RecordingProfileCleaner cleaner = new();
        using WebViewSessionManager manager = CreateManager(environments, cleaner);
        ServiceInstance first = CreateService(ServiceType.Telegram);
        ServiceInstance waiting = CreateService(ServiceType.WhatsApp);

        Task<bool> firstInitialization = manager.InitializeAsync((IntPtr)1, Bounds, first, activate: false);
        Task<bool> waitingInitialization = manager.InitializeAsync((IntPtr)1, Bounds, waiting, activate: false);
        manager.ReleaseSession(waiting.Id);
        firstEnvironment.Fail(new IOException("controlled initialization failure"));

        Assert.False(await firstInitialization);
        Assert.False(await waitingInitialization);
        Assert.Equal(1, environments.RequestCount);
        Assert.False(manager.HasSession(waiting.Id));
    }

    [Fact]
    public async Task ReleaseDuringPendingInitialization_PreventsLateStateResurrection()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        using WebViewSessionManager manager = CreateManager(environments, new RecordingProfileCleaner());
        ServiceInstance service = CreateService(ServiceType.Telegram);
        List<WebViewSessionStatus> lateStates = [];
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: true);

        manager.ReleaseSession(service.Id);
        manager.StateChanged += (_, args) => lateStates.Add(args.State.Status);
        pending.Fail(new IOException("late controlled failure"));

        Assert.False(await initialization);
        Assert.Empty(lateStates);
        Assert.Equal(WebViewSessionStatus.Uninitialized, manager.State.Status);
        Assert.False(manager.HasSession(service.Id));
        Assert.False(manager.IsSessionInitialized(service.Id));
    }

    [Fact]
    public async Task ResourceCreatedAfterRelease_IsClosedInsteadOfAttached()
    {
        WebViewSessionLifetime lifetime = new();
        TaskCompletionSource<bool> creation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeController controller = new();
        bool attached = false;
        Task<bool> initialization = lifetime.GetOrStartInitialization(async _ =>
        {
            await creation.Task;
            return lifetime.TryAttachResource(
                controller,
                _ => attached = true,
                resource => resource.Close());
        });

        Task settlement = lifetime.BeginRelease();
        creation.SetResult(true);

        Assert.False(await initialization);
        await settlement;
        Assert.False(attached);
        Assert.Equal(1, controller.CloseCount);
    }

    [Fact]
    public async Task ReleaseThenInitializeSameService_UsesIndependentEntry()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment oldEnvironment = environments.Enqueue();
        PendingEnvironment replacementEnvironment = environments.Enqueue();
        using WebViewSessionManager manager = CreateManager(environments, new RecordingProfileCleaner());
        ServiceInstance service = CreateService(ServiceType.Telegram);
        Task<bool> oldInitialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false);

        manager.ReleaseSession(service.Id);
        Task<bool> replacementInitialization = manager.InitializeAsync(
            (IntPtr)1,
            Bounds,
            service,
            activate: false);
        oldEnvironment.Fail(new IOException("old entry failure"));

        Assert.False(await oldInitialization);
        await WaitForAsync(() => environments.RequestCount == 2);
        Assert.True(manager.HasSession(service.Id));
        replacementEnvironment.Fail(new IOException("replacement entry failure"));
        Assert.False(await replacementInitialization);
        Assert.True(manager.HasSession(service.Id));
    }

    [Theory]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    [InlineData(ServiceType.VkMessenger)]
    public async Task ReleaseOneAccount_DoesNotReleaseAnotherServiceEntry(ServiceType otherServiceType)
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment telegramEnvironment = environments.Enqueue();
        PendingEnvironment otherEnvironment = environments.Enqueue();
        using WebViewSessionManager manager = CreateManager(environments, new RecordingProfileCleaner());
        ServiceInstance telegram = CreateService(ServiceType.Telegram);
        ServiceInstance otherService = CreateService(otherServiceType);
        Task<bool> telegramInitialization = manager.InitializeAsync(
            (IntPtr)1,
            Bounds,
            telegram,
            activate: false);
        Task<bool> otherInitialization = manager.InitializeAsync(
            (IntPtr)1,
            Bounds,
            otherService,
            activate: false);

        manager.ReleaseSession(telegram.Id);

        Assert.False(manager.HasSession(telegram.Id));
        Assert.True(manager.HasSession(otherService.Id));
        telegramEnvironment.Fail(new IOException("telegram entry failure"));
        Assert.False(await telegramInitialization);
        await WaitForAsync(() => environments.RequestCount == 2);
        otherEnvironment.Fail(new IOException("other service entry failure"));
        Assert.False(await otherInitialization);
        Assert.True(manager.HasSession(otherService.Id));
    }

    [Fact]
    public async Task RepeatedRelease_IsIdempotent()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        using WebViewSessionManager manager = CreateManager(environments, new RecordingProfileCleaner());
        ServiceInstance service = CreateService(ServiceType.Telegram);
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false);

        manager.ReleaseSession(service.Id);
        manager.ReleaseSession(service.Id);
        Task releaseSettlement = manager.ReleaseSessionAsync(service.Id);
        Assert.False(releaseSettlement.IsCompleted);
        pending.Fail(new IOException("controlled failure"));

        Assert.False(await initialization);
        await releaseSettlement;
        Assert.False(manager.HasSession(service.Id));
    }

    [Fact]
    public async Task ClearProfile_WaitsForInitializationSettlementBeforeFilesystemDelete()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        RecordingProfileCleaner cleaner = new();
        using WebViewSessionManager manager = CreateManager(environments, cleaner);
        ServiceInstance service = CreateService(ServiceType.Telegram);
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false);

        Task<bool> clear = manager.ClearProfileAsync(service);

        Assert.False(clear.IsCompleted);
        Assert.Equal(0, cleaner.DeleteCount);
        Assert.False(manager.HasSession(service.Id));
        pending.Fail(new IOException("controlled failure"));
        Assert.False(await initialization);
        Assert.True(await clear);
        Assert.Equal(1, cleaner.DeleteCount);
        Assert.Equal(service.Id, cleaner.LastServiceInstanceId);
        Assert.Equal(service.ProfileName, cleaner.LastProfileName);
    }

    [Fact]
    public async Task ClearProfileAfterEarlierRelease_WaitsForRetiredEntrySettlement()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        RecordingProfileCleaner cleaner = new();
        using WebViewSessionManager manager = CreateManager(environments, cleaner);
        ServiceInstance service = CreateService(ServiceType.Telegram);
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false);
        manager.ReleaseSession(service.Id);

        Task<bool> clear = manager.ClearProfileAsync(service);

        Assert.False(clear.IsCompleted);
        Assert.Equal(0, cleaner.DeleteCount);
        pending.Fail(new IOException("retired entry failure"));
        Assert.False(await initialization);
        Assert.True(await clear);
        Assert.Equal(1, cleaner.DeleteCount);
    }

    [Fact]
    public async Task ClearProfile_BlocksReplacementUntilSettlementAndDeleteComplete()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        RecordingProfileCleaner cleaner = new();
        using WebViewSessionManager manager = CreateManager(environments, cleaner);
        ServiceInstance service = CreateService(ServiceType.Telegram);
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false);
        Task<bool> clear = manager.ClearProfileAsync(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InitializeAsync((IntPtr)1, Bounds, service, activate: false));

        pending.Fail(new IOException("controlled failure"));
        Assert.False(await initialization);
        Assert.True(await clear);
    }

    [Fact]
    public async Task ShutdownDuringPendingInitialization_IsSafeAndSuppressesLateState()
    {
        ControlledEnvironmentProvider environments = new();
        PendingEnvironment pending = environments.Enqueue();
        using WebViewSessionManager manager = CreateManager(environments, new RecordingProfileCleaner());
        ServiceInstance service = CreateService(ServiceType.Telegram);
        List<WebViewSessionStatus> lateStates = [];
        Task<bool> initialization = manager.InitializeAsync((IntPtr)1, Bounds, service, activate: true);

        manager.BeginShutdown();
        manager.StateChanged += (_, args) => lateStates.Add(args.State.Status);
        pending.Fail(new IOException("late shutdown failure"));

        Assert.False(await initialization);
        Assert.Empty(lateStates);
        Assert.True(manager.IsShutdownStarted);
        Assert.False(manager.HasSession(service.Id));
        Assert.Equal(0, manager.InitializedSessionCount);
    }

    private static WebViewSessionManager CreateManager(
        ICoreWebView2EnvironmentProvider environmentProvider,
        IWebViewProfileCleaner cleaner)
    {
        BuiltInServiceCatalog catalog = new();
        NavigationPolicy navigationPolicy = new(catalog);
        WebNavigationService navigation = new(
            navigationPolicy,
            new NoOpExternalBrowserService(),
            new ExternalBrowserLaunchPolicy());
        return new WebViewSessionManager(
            environmentProvider,
            catalog,
            navigationPolicy,
            navigation,
            new WebNewWindowNavigationService(navigation),
            new StubPermissionCoordinator(),
            cleaner);
    }

    private static ServiceInstance CreateService(ServiceType serviceType)
    {
        Guid id = Guid.NewGuid();
        string startUrl = serviceType switch
        {
            ServiceType.Telegram => "https://web.telegram.org/k/",
            ServiceType.WhatsApp => "https://web.whatsapp.com/",
            ServiceType.Max => "https://web.max.ru/",
            ServiceType.VkMessenger => "https://web.vk.me/",
            _ => throw new ArgumentOutOfRangeException(nameof(serviceType))
        };
        return new ServiceInstance
        {
            Id = id,
            ServiceType = serviceType,
            DisplayName = serviceType.ToString(),
            StartUrl = startUrl,
            ProfileName = ProfileNameFactory.Create(id),
            IsEnabled = true
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeController
    {
        public int CloseCount { get; private set; }
        public void Close() => CloseCount++;
    }

    private sealed class ControlledEnvironmentProvider : ICoreWebView2EnvironmentProvider
    {
        private readonly Queue<PendingEnvironment> _pending = [];

        public int RequestCount { get; private set; }

        public PendingEnvironment Enqueue()
        {
            PendingEnvironment pending = new();
            _pending.Enqueue(pending);
            return pending;
        }

        public Task<CoreWebView2Environment> GetAsync()
        {
            RequestCount++;
            return _pending.Dequeue().Task;
        }
    }

    private sealed class PendingEnvironment
    {
        private readonly TaskCompletionSource<CoreWebView2Environment> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CoreWebView2Environment> Task => _completion.Task;

        public void Fail(Exception exception) => _completion.SetException(exception);
    }

    private sealed class RecordingProfileCleaner : IWebViewProfileCleaner
    {
        public int DeleteCount { get; private set; }
        public Guid? LastServiceInstanceId { get; private set; }
        public string? LastProfileName { get; private set; }

        public Task<bool> TryDeleteProfileAsync(
            Guid serviceInstanceId,
            string profileName,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            LastServiceInstanceId = serviceInstanceId;
            LastProfileName = profileName;
            return Task.FromResult(true);
        }

        public Task<bool> ProcessPendingDeletionsAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpExternalBrowserService : IExternalBrowserService
    {
        public bool TryOpen(Uri uri) => true;
    }

    private sealed class StubPermissionCoordinator : INotificationPermissionCoordinator
    {
        public Task<NotificationPermissionState> DecideAsync(
            ServiceInstance service,
            string? senderOrigin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationPermissionState.Denied);

        public Task<NotificationPermissionState> SynchronizeFromProfileAsync(
            ServiceInstance service,
            string? permissionOrigin,
            NotificationPermissionState profileState,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profileState);
    }
}
