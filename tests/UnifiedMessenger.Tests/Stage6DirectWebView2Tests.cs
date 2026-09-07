using System.Drawing;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class Stage6DirectWebView2Tests
{
    private static readonly Rectangle ValidBounds = new(84, 64, 1096, 696);

    [Fact]
    public async Task StartupPrime_SelectedFirst_ThenEnabledInUserOrder_Sequentially()
    {
        ServiceInstance telegram = CreateService(ServiceType.Telegram);
        ServiceInstance whatsapp = CreateService(ServiceType.WhatsApp);
        ServiceInstance max = CreateService(ServiceType.Max);
        ServiceInstance vk = CreateService(ServiceType.VkMessenger);
        ServiceInstance disabled = CreateService(ServiceType.Telegram, enabled: false);
        RecordingSessionManager sessions = new();
        WebViewStartupPrimeCoordinator coordinator = new(sessions);

        StartupPrimeResult result = await coordinator.PrimeAsync(
            new IntPtr(42),
            ValidBounds,
            [telegram, whatsapp, max, disabled, vk, telegram],
            max.Id);

        Assert.Equal([max.Id, telegram.Id, whatsapp.Id, vk.Id], result.AttemptedServiceIds);
        Assert.Equal(result.AttemptedServiceIds, result.InitializedServiceIds);
        Assert.Empty(result.FailedServiceIds);
        Assert.Equal(1, sessions.MaximumConcurrentPrimeCalls);
        Assert.All(result.AttemptedServiceIds, id => Assert.Equal(1, sessions.PrimeCalls.Count(value => value == id)));
        Assert.Equal(4, sessions.InitialNavigationCount);
        Assert.Equal(4, sessions.InitializedSessionCount);
    }

    [Fact]
    public async Task StartupPrime_FailedService_DoesNotBlockRemainingServices()
    {
        ServiceInstance first = CreateService(ServiceType.Max);
        ServiceInstance failed = CreateService(ServiceType.Telegram);
        ServiceInstance last = CreateService(ServiceType.WhatsApp);
        RecordingSessionManager sessions = new()
        {
            PrimeBehavior = (service, _) => Task.FromResult(service.Id != failed.Id)
        };
        WebViewStartupPrimeCoordinator coordinator = new(sessions);

        StartupPrimeResult result = await coordinator.PrimeAsync(
            new IntPtr(42),
            ValidBounds,
            [first, failed, last],
            first.Id);

        Assert.Equal([first.Id, failed.Id, last.Id], result.AttemptedServiceIds);
        Assert.Equal([first.Id, last.Id], result.InitializedServiceIds);
        Assert.Equal([failed.Id], result.FailedServiceIds);
    }

    [Fact]
    public async Task StartupPrime_Cancellation_StopsBeforeAnotherControllerIsCreated()
    {
        ServiceInstance first = CreateService(ServiceType.Max);
        ServiceInstance second = CreateService(ServiceType.Telegram);
        RecordingSessionManager sessions = new()
        {
            PrimeBehavior = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
        };
        WebViewStartupPrimeCoordinator coordinator = new(sessions);
        using CancellationTokenSource cancellation = new();

        Task prime = coordinator.PrimeAsync(
            new IntPtr(42),
            ValidBounds,
            [first, second],
            first.Id,
            cancellationToken: cancellation.Token);
        await sessions.FirstPrimeStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prime);
        Assert.Equal([first.Id], sessions.PrimeCalls);
    }

    [Fact]
    public async Task StartupPrime_PreservesIdentityProfilePermissionAndRuntimeActivity()
    {
        ServiceInstance telegram = CreateService(ServiceType.Telegram);
        telegram.NotificationPermissionState = NotificationPermissionState.Allowed;
        telegram.UnreadCount = 3;
        telegram.HasUnreadActivity = true;
        Guid id = telegram.Id;
        string profileName = telegram.ProfileName;
        RecordingSessionManager sessions = new();
        WebViewStartupPrimeCoordinator coordinator = new(sessions);

        await coordinator.PrimeAsync(
            new IntPtr(42),
            ValidBounds,
            [telegram],
            telegram.Id);

        Assert.Equal(id, telegram.Id);
        Assert.Equal(profileName, telegram.ProfileName);
        Assert.Equal(NotificationPermissionState.Allowed, telegram.NotificationPermissionState);
        Assert.Equal(3, telegram.UnreadCount);
        Assert.True(telegram.HasUnreadActivity);
    }

    [Fact]
    public void SwitchingAndWindowLayout_DoNotInitializeOrNavigateAgain()
    {
        ServiceInstance service = CreateService(ServiceType.VkMessenger);
        RecordingSessionManager sessions = new();

        sessions.ActivateSession(service.Id, ValidBounds, isVisible: true, moveFocus: true);
        sessions.UpdateActiveSessionLayout(new Rectangle(84, 64, 900, 600), isVisible: true);
        sessions.NotifyParentWindowPositionChanged();

        Assert.Empty(sessions.PrimeCalls);
        Assert.Equal(0, sessions.InitializeCalls);
        Assert.Equal(0, sessions.InitialNavigationCount);
        Assert.Equal(1, sessions.ActivateCalls);
        Assert.Equal(1, sessions.LayoutCalls);
        Assert.Equal(1, sessions.ParentPositionChangeCalls);
    }

    [Theory]
    [InlineData(false, false, false, EnabledAccountInitializationAction.None)]
    [InlineData(true, true, true, EnabledAccountInitializationAction.None)]
    [InlineData(true, false, false, EnabledAccountInitializationAction.PrimeWhileHidden)]
    [InlineData(true, false, true, EnabledAccountInitializationAction.WaitForSelectionOrHiddenState)]
    public void EnableAfterStartupPolicy_IsInvisibleWhenPossibleAndNeverCyclesVisibleWindow(
        bool enabled,
        bool initialized,
        bool mainWindowVisible,
        EnabledAccountInitializationAction expected)
    {
        Assert.Equal(
            expected,
            EnabledAccountInitializationPolicy.Decide(enabled, initialized, mainWindowVisible));
    }

    [Fact]
    public void ShutdownCloseGuard_IsIdempotent()
    {
        WebViewControllerCloseGuard guard = new();

        Assert.True(guard.TryBeginClose());
        Assert.False(guard.TryBeginClose());
        Assert.False(guard.TryBeginClose());
    }

    [Fact]
    public void ProductionSessionContract_HasNoWpfOrCompositionHostingSurface()
    {
        Type[] contractTypes = typeof(IWebViewSessionManager)
            .GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .ToArray();

        Assert.DoesNotContain(contractTypes, type =>
            type.FullName?.Contains("Microsoft.Web.WebView2.Wpf", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(contractTypes, type =>
            type.Name.Contains("CompositionControl", StringComparison.Ordinal));
        Assert.Contains(contractTypes, type => type == typeof(IntPtr));
        Assert.Contains(contractTypes, type => type == typeof(Rectangle));
    }

    private static ServiceInstance CreateService(ServiceType serviceType, bool enabled = true)
    {
        Guid id = Guid.NewGuid();
        string startUrl = serviceType switch
        {
            ServiceType.Telegram => "https://web.telegram.org/",
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
            IsEnabled = enabled
        };
    }

    private sealed class RecordingSessionManager : IWebViewSessionManager
    {
        private readonly HashSet<Guid> _initialized = [];
        private int _activePrimeCalls;

        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested { add { } remove { } }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged { add { } remove { } }
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived { add { } remove { } }
        public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived { add { } remove { } }

        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted { get; private set; }
        public int InitializedSessionCount => _initialized.Count;
        public int InitialNavigationCount { get; private set; }
        public int InitializeCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int LayoutCalls { get; private set; }
        public int ParentPositionChangeCalls { get; private set; }
        public int MaximumConcurrentPrimeCalls { get; private set; }
        public List<Guid> PrimeCalls { get; } = [];
        public TaskCompletionSource FirstPrimeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<ServiceInstance, CancellationToken, Task<bool>> PrimeBehavior { get; init; } =
            static (_, _) => Task.FromResult(true);

        public Task<bool> InitializeAsync(
            IntPtr parentWindow,
            Rectangle bounds,
            ServiceInstance serviceInstance,
            bool activate,
            CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            if (_initialized.Add(serviceInstance.Id))
            {
                InitialNavigationCount++;
            }

            return Task.FromResult(true);
        }

        public async Task<bool> PrimeAsync(
            IntPtr parentWindow,
            Rectangle bounds,
            ServiceInstance serviceInstance,
            CancellationToken cancellationToken = default)
        {
            PrimeCalls.Add(serviceInstance.Id);
            int concurrent = Interlocked.Increment(ref _activePrimeCalls);
            MaximumConcurrentPrimeCalls = Math.Max(MaximumConcurrentPrimeCalls, concurrent);
            FirstPrimeStarted.TrySetResult();
            try
            {
                bool result = await PrimeBehavior(serviceInstance, cancellationToken);
                if (result && _initialized.Add(serviceInstance.Id))
                {
                    InitialNavigationCount++;
                }

                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _activePrimeCalls);
            }
        }

        public bool IsSessionInitialized(Guid serviceInstanceId) => _initialized.Contains(serviceInstanceId);
        public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false) => ActivateCalls++;
        public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible) => LayoutCalls++;
        public void NotifyParentWindowPositionChanged() => ParentPositionChangeCalls++;
        public bool HasSession(Guid serviceInstanceId) => _initialized.Contains(serviceInstanceId);
        public void DeactivateSession() { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId) => _initialized.Remove(serviceInstanceId);
        public Task ReleaseSessionAsync(Guid serviceInstanceId, CancellationToken cancellationToken = default)
        {
            ReleaseSession(serviceInstanceId);
            return Task.CompletedTask;
        }
        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default)
        {
            _initialized.Remove(serviceInstance.Id);
            return Task.FromResult(true);
        }
        public void ReleaseAllSessions() => _initialized.Clear();
        public void BeginShutdown() => IsShutdownStarted = true;
        public void Dispose() => BeginShutdown();
    }
}
