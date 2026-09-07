using System.Drawing;
using System.IO;
using System.Text.Json;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage5SettingsTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Fact]
    public void SettingsViewModel_LoadsCurrentValuesAndExistingAccounts()
    {
        using SettingsFixture fixture = CreateFixture();
        fixture.Settings.CloseToTray = false;
        fixture.Settings.HasShownTrayHint = true;
        fixture.Settings.Notifications.IsEnabled = false;
        fixture.Settings.Notifications.DoNotDisturb = true;
        fixture.Settings.Notifications.ShowNotificationPreview = false;
        fixture.Settings.Notifications.PlaySound = false;

        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        Assert.False(viewModel.CloseToTray);
        Assert.True(viewModel.HasShownTrayHint);
        Assert.False(viewModel.NotificationsEnabled);
        Assert.True(viewModel.DoNotDisturb);
        Assert.False(viewModel.ShowNotificationPreview);
        Assert.False(viewModel.NotificationSoundEnabled);
        Assert.Equal(fixture.Main.Services.Count, viewModel.Accounts.Count);
        Assert.Same(fixture.Main.Services[0], viewModel.Accounts[0].Service);
    }

    [Fact]
    public async Task CloseToTray_ChangesAndPersistsImmediately()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.SetCloseToTrayCommand.ExecuteAsync(false);

        Assert.False(fixture.Settings.CloseToTray);
        Assert.False(viewModel.CloseToTray);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task NotificationsEnabled_ChangesAndPersistsImmediately()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.SetNotificationsEnabledCommand.ExecuteAsync(false);

        Assert.False(fixture.Settings.Notifications.IsEnabled);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task DoNotDisturb_ChangesAndPersistsImmediately()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.SetDoNotDisturbCommand.ExecuteAsync(true);

        Assert.True(fixture.Settings.Notifications.DoNotDisturb);
        Assert.True(fixture.Main.DoNotDisturb);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task SettingsViewModel_ReflectsDoNotDisturbChangedFromTrayPath()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        await fixture.Main.SetDoNotDisturbAsync(true);

        Assert.True(viewModel.DoNotDisturb);
        Assert.Contains(nameof(SettingsViewModel.DoNotDisturb), changedProperties);
    }

    [Fact]
    public async Task ShowNotificationPreview_ChangesAndPersistsImmediately()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.SetShowNotificationPreviewCommand.ExecuteAsync(false);

        Assert.False(fixture.Settings.Notifications.ShowNotificationPreview);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task NotificationSoundEnabled_ChangesAndPersistsImmediately()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.SetNotificationSoundEnabledCommand.ExecuteAsync(false);

        Assert.False(fixture.Settings.Notifications.PlaySound);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task ResetTrayHint_ChangesOnlyHintStateAndPersists()
    {
        using SettingsFixture fixture = CreateFixture();
        fixture.Settings.HasShownTrayHint = true;
        bool originalCloseToTray = fixture.Settings.CloseToTray;
        bool originalDnd = fixture.Settings.Notifications.DoNotDisturb;
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();

        await viewModel.ResetTrayHintCommand.ExecuteAsync(null);

        Assert.False(fixture.Settings.HasShownTrayHint);
        Assert.Equal(originalCloseToTray, fixture.Settings.CloseToTray);
        Assert.Equal(originalDnd, fixture.Settings.Notifications.DoNotDisturb);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public void CloseToTrayDisabled_RequestsFullExitInsteadOfHide()
    {
        ApplicationExitCoordinator coordinator = new();

        Assert.False(coordinator.ShouldHideToTray(closeToTray: false, applicationShutdownStarted: false));
        Assert.True(coordinator.ShouldRequestExitFromWindowClose(closeToTray: false, applicationShutdownStarted: false));

        coordinator.RequestExit();

        Assert.True(coordinator.IsExplicitExitRequested);
        Assert.False(coordinator.ShouldRequestExitFromWindowClose(closeToTray: false, applicationShutdownStarted: false));
    }

    [Fact]
    public void SoundDisabled_KeepsPopupAndActivityButDoesNotPlaySystemSound()
    {
        ServiceInstance service = CreateService(ServiceType.WhatsApp);
        AppSettings settings = new() { Services = [service] };
        settings.Notifications.PlaySound = false;
        FakeSettingsStore store = new(settings);
        FakeNotificationPopupService popup = new();
        FakeTrayIconService tray = new();
        FakeWindowActivationService window = new();
        ServiceActivityCoordinator activity = new();
        FakeNotificationSoundPlayer player = new();
        using TelegramNotificationSoundCoordinator sound = new(
            store,
            player,
            new ImmediateDispatcher(),
            TimeProvider.System);
        using WebNotificationCoordinator coordinator = new(
            popup,
            tray,
            window,
            store,
            new NavigationPolicy(_catalog),
            activity,
            _catalog,
            sound);
        WebNotificationLifecycle lifecycle = new(() => { }, () => { }, () => { });

        coordinator.Handle(
            new WebNotificationRequest(
                service.Id,
                "https://web.whatsapp.com/",
                "Runtime-only title",
                "Runtime-only body",
                lifecycle));

        Assert.Single(popup.Shown);
        Assert.True(service.HasUnreadActivity);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task PerAccountMute_UsesExistingMainAccountOperation()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        SettingsAccountViewModel account = viewModel.Accounts[0];

        await account.SetMutedCommand.ExecuteAsync(true);

        Assert.True(account.Service.IsMuted);
        Assert.Contains(account.Service.Id, fixture.Notifications.DiscardedServiceIds);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task RenameRequest_UsesExistingAccountBusinessLogic()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        Task renameTask = Task.CompletedTask;
        viewModel.RenameAccountRequested += (_, eventArgs) =>
            renameTask = fixture.Main.RenameServiceAsync(eventArgs.Service, "Рабочий Telegram");

        viewModel.Accounts[0].RenameCommand.Execute(null);
        await renameTask;

        Assert.Equal("Рабочий Telegram", fixture.Main.Services[0].DisplayName);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task EnableDisableRequest_UsesExistingAccountBusinessLogic()
    {
        using SettingsFixture fixture = CreateFixture();
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        Task updateTask = Task.CompletedTask;
        viewModel.AccountEnabledChangeRequested += (_, eventArgs) =>
            updateTask = fixture.Main.SetServiceEnabledAsync(eventArgs.Service, eventArgs.IsEnabled);

        viewModel.Accounts[0].SetEnabledCommand.Execute(false);
        await updateTask;

        Assert.False(fixture.Main.Services[0].IsEnabled);
        Assert.Same(fixture.Main.Services[0], fixture.Main.SelectedService);
        Assert.True(fixture.Main.IsSelectedServiceDisabled);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal([fixture.Main.Services[0].Id], fixture.Session.ReleaseSessionRequests);
        Assert.Equal(0, fixture.Session.ClearProfileCount);
    }

    [Fact]
    public async Task DisablingTelegram_ReleasesOnlyTargetSessionAndPreservesEveryAccountIdentity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance telegramA = CreateService(ServiceType.Telegram);
        ServiceInstance telegramB = CreateService(ServiceType.Telegram);
        ServiceInstance whatsapp = CreateService(ServiceType.WhatsApp);
        ServiceInstance max = CreateService(ServiceType.Max);
        ServiceInstance vk = CreateService(ServiceType.VkMessenger);
        settings.Services.AddRange([telegramA, telegramB, whatsapp, max, vk]);
        MailAccount gmail = new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            EmailAddress = "telegram-lifecycle@gmail.test",
            CredentialKey = "gmail-credential",
            IsEnabled = true
        };
        settings.MailAccounts.Add(gmail);
        using SettingsFixture fixture = new(settings, _catalog);
        foreach (ServiceInstance service in settings.Services)
        {
            fixture.Session.AddSession(service.Id);
        }

        Guid originalId = telegramA.Id;
        string originalProfile = telegramA.ProfileName;

        await fixture.Main.SetServiceEnabledAsync(telegramA, isEnabled: false);

        Assert.False(telegramA.IsEnabled);
        Assert.Equal(originalId, telegramA.Id);
        Assert.Equal(originalProfile, telegramA.ProfileName);
        Assert.Equal([telegramA.Id], fixture.Session.ReleaseSessionRequests);
        Assert.False(fixture.Session.HasSession(telegramA.Id));
        Assert.All(
            new[] { telegramB, whatsapp, max, vk },
            service => Assert.True(fixture.Session.HasSession(service.Id)));
        Assert.True(gmail.IsEnabled);
        Assert.Equal(0, fixture.Session.ClearProfileCount);
        Assert.DoesNotContain(originalProfile, settings.PendingProfileDeletions);
    }

    [Fact]
    public async Task DisablingTelegramWithoutCreatedSession_IsSafeAndDoesNotClearProfile()
    {
        using SettingsFixture fixture = CreateFixture();
        ServiceInstance telegram = fixture.Main.Services[0];

        await fixture.Main.SetServiceEnabledAsync(telegram, isEnabled: false);

        Assert.False(telegram.IsEnabled);
        Assert.False(fixture.Session.HasSession(telegram.Id));
        Assert.Equal([telegram.Id], fixture.Session.ReleaseSessionRequests);
        Assert.Equal(0, fixture.Session.ClearProfileCount);
        Assert.Empty(fixture.Settings.PendingProfileDeletions);
    }

    [Fact]
    public async Task ReenablingTelegram_PreservesIdentityAndAllowsNormalSessionCreation()
    {
        using SettingsFixture fixture = CreateFixture();
        ServiceInstance telegram = fixture.Main.Services[0];
        Guid originalId = telegram.Id;
        string originalProfile = telegram.ProfileName;
        fixture.Session.AddSession(telegram.Id);

        await fixture.Main.SetServiceEnabledAsync(telegram, isEnabled: false);
        await fixture.Main.SetServiceEnabledAsync(telegram, isEnabled: true);

        Assert.True(telegram.IsEnabled);
        Assert.Equal(originalId, telegram.Id);
        Assert.Equal(originalProfile, telegram.ProfileName);
        Assert.False(fixture.Session.IsSessionInitialized(telegram.Id));

        await fixture.Session.InitializeAsync(
            new IntPtr(42),
            new Rectangle(0, 0, 800, 600),
            telegram,
            activate: true);

        Assert.True(fixture.Session.IsSessionInitialized(telegram.Id));
        Assert.Same(telegram, fixture.Session.LastInitializedService);
        Assert.Equal(originalProfile, fixture.Session.LastInitializedService!.ProfileName);
        Assert.Equal(0, fixture.Session.ClearProfileCount);
    }

    [Fact]
    public async Task RepeatedTelegramEnableDisableCycles_KeepOnlyOneCurrentSession()
    {
        using SettingsFixture fixture = CreateFixture();
        ServiceInstance telegram = fixture.Main.Services[0];
        Guid originalId = telegram.Id;
        string originalProfile = telegram.ProfileName;
        fixture.Session.AddSession(telegram.Id);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            await fixture.Main.SetServiceEnabledAsync(telegram, isEnabled: false);
            Assert.False(fixture.Session.HasSession(telegram.Id));

            await fixture.Main.SetServiceEnabledAsync(telegram, isEnabled: true);
            await fixture.Session.InitializeAsync(
                new IntPtr(42),
                new Rectangle(0, 0, 800, 600),
                telegram,
                activate: true);
        }

        Assert.Equal(3, fixture.Session.ReleaseSessionRequests.Count);
        Assert.All(fixture.Session.ReleaseSessionRequests, id => Assert.Equal(originalId, id));
        Assert.Equal(1, fixture.Session.InitializedSessionCount);
        Assert.Equal(originalId, telegram.Id);
        Assert.Equal(originalProfile, telegram.ProfileName);
        Assert.Equal(0, fixture.Session.ClearProfileCount);
    }

    [Fact]
    public async Task DeleteAfterDisabledTelegram_CleansOnlyItsPreservedProfile()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance target = CreateService(ServiceType.Telegram);
        ServiceInstance other = CreateService(ServiceType.Telegram);
        settings.Services.AddRange([target, other]);
        using SettingsFixture fixture = new(settings, _catalog);
        fixture.Session.AddSession(target.Id);
        fixture.Session.AddSession(other.Id);
        string targetProfile = target.ProfileName;

        await fixture.Main.SetServiceEnabledAsync(target, isEnabled: false);
        await DeleteThroughExistingLifecycleAsync(fixture, target);

        Assert.Equal(1, fixture.Session.ClearProfileCount);
        Assert.Equal(target.Id, fixture.Session.LastClearedServiceId);
        Assert.Equal(targetProfile, fixture.Session.LastClearedProfileName);
        Assert.DoesNotContain(fixture.Main.Services, service => service.Id == target.Id);
        Assert.True(fixture.Session.HasSession(other.Id));
        Assert.DoesNotContain(targetProfile, fixture.Settings.PendingProfileDeletions);
    }

    [Theory]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    [InlineData(ServiceType.VkMessenger)]
    public async Task DisablingAnotherWebService_DoesNotApplyTelegramReleasePolicy(ServiceType serviceType)
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(serviceType);
        settings.Services.Add(service);
        using SettingsFixture fixture = new(settings, _catalog);
        fixture.Session.AddSession(service.Id);

        await fixture.Main.SetServiceEnabledAsync(service, isEnabled: false);

        Assert.False(service.IsEnabled);
        Assert.Empty(fixture.Session.ReleaseSessionRequests);
        Assert.True(fixture.Session.HasSession(service.Id));
        Assert.Equal(0, fixture.Session.ClearProfileCount);
    }

    [Fact]
    public async Task Reorder_UsesExistingAccountBusinessLogic()
    {
        using SettingsFixture fixture = CreateFixture(includeSecondAccount: true);
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        Guid firstId = viewModel.Accounts[0].Service.Id;
        Guid secondId = viewModel.Accounts[1].Service.Id;

        await viewModel.Accounts[0].MoveDownCommand.ExecuteAsync(null);

        Assert.Equal([secondId, firstId], fixture.Main.Services.Select(service => service.Id));
        Assert.Equal([0, 1], fixture.Main.Services.Select(service => service.SortOrder));
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task DeleteRequest_UsesExistingProfileLifecycle()
    {
        using SettingsFixture fixture = CreateFixture(includeSecondAccount: true);
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        ServiceInstance target = viewModel.Accounts[0].Service;
        Task deleteTask = Task.CompletedTask;
        viewModel.DeleteAccountRequested += (_, eventArgs) =>
            deleteTask = DeleteThroughExistingLifecycleAsync(fixture, eventArgs.Service);

        viewModel.Accounts[0].DeleteCommand.Execute(null);
        await deleteTask;

        Assert.Equal(target.Id, fixture.Session.LastClearedServiceId);
        Assert.Equal(target.ProfileName, fixture.Session.LastClearedProfileName);
        Assert.DoesNotContain(fixture.Main.Services, service => service.Id == target.Id);
        Assert.DoesNotContain(fixture.Settings.PendingProfileDeletions, profile => profile == target.ProfileName);
    }

    [Fact]
    public void NavigationFromSettingsToService_ReusesSameServiceInstance()
    {
        using SettingsFixture fixture = CreateFixture(includeSecondAccount: true);
        using SettingsViewModel viewModel = fixture.CreateSettingsViewModel();
        ServiceInstance target = fixture.Main.Services[1];
        fixture.Main.OpenSettingsCommand.Execute(null);

        viewModel.Accounts[1].OpenCommand.Execute(null);

        Assert.False(fixture.Main.IsSettingsOpen);
        Assert.Same(target, fixture.Main.SelectedService);
        Assert.Equal(0, fixture.Session.InitializeCount);
        Assert.Equal(target.Id, fixture.Settings.LastServiceId);
        Assert.Equal(target.ProfileName, fixture.Main.SelectedService!.ProfileName);
    }

    [Fact]
    public void OpeningSettings_DoesNotDeactivateReleaseOrRecreateWebViewSession()
    {
        using SettingsFixture fixture = CreateFixture();
        ServiceInstance selected = fixture.Main.SelectedService!;

        fixture.Main.OpenSettingsCommand.Execute(null);

        Assert.True(fixture.Main.IsSettingsOpen);
        Assert.Same(selected, fixture.Main.SelectedService);
        Assert.Equal(0, fixture.Session.InitializeCount);
        Assert.Equal(0, fixture.Session.DeactivateCount);
        Assert.Equal(0, fixture.Session.ReleaseSessionCount);
        Assert.Equal(selected.Id, fixture.Main.SelectedService!.Id);
        Assert.Equal(selected.ProfileName, fixture.Main.SelectedService.ProfileName);
    }

    [Fact]
    public void SettingsScreen_DoesNotMarkHiddenSelectedServiceAsViewed()
    {
        using SettingsFixture fixture = CreateFixture();
        ServiceInstance selected = fixture.Main.SelectedService!;
        fixture.Activity.MarkNotificationReceived(selected);
        fixture.Main.OpenSettingsCommand.Execute(null);

        fixture.Main.MarkSelectedServiceViewed(isMainWindowVisible: true, isMainWindowActive: true);

        Assert.True(selected.HasUnreadActivity);

        fixture.Main.CloseSettingsCommand.Execute(null);
        fixture.Main.MarkSelectedServiceViewed(isMainWindowVisible: true, isMainWindowActive: true);

        Assert.False(selected.HasUnreadActivity);
    }

    [Fact]
    public void TraySettingsRequest_OpensTheSameSettingsScreen()
    {
        using SettingsFixture fixture = CreateFixture();
        FakeTrayIconService tray = new();
        FakeWindowActivationService window = new();
        using ApplicationTrayCoordinator coordinator = new(
            tray,
            window,
            new ApplicationExitCoordinator(),
            fixture.Notifications,
            new ServiceActivityCoordinator(),
            fixture.Store,
            fixture.Main,
            new ImmediateDispatcher(),
            new FakeTaskbarActivityIndicator());
        coordinator.Initialize();

        tray.RaiseSettings();

        Assert.True(fixture.Main.IsSettingsOpen);
        Assert.Equal(1, window.ShowCount);
    }

    [Fact]
    public async Task SchemaMigration_PreservesIdsProfilesAndAccountSettings()
    {
        using TempSettingsFolder temp = new();
        Guid id = Guid.NewGuid();
        string profileName = ProfileNameFactory.Create(id);
        string json = $$"""
            {
              "schemaVersion": 2,
              "lastServiceId": "{{id}}",
              "closeToTray": false,
              "notifications": {
                "isEnabled": true,
                "showNotificationPreview": false,
                "playSound": false,
                "doNotDisturb": true
              },
              "services": [
                {
                  "id": "{{id}}",
                  "serviceType": "Telegram",
                  "displayName": "Telegram",
                  "profileName": "{{profileName}}",
                  "isEnabled": false,
                  "sortOrder": 4,
                  "isMuted": true,
                  "notificationPermissionState": "Allowed"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(temp.SettingsPath, json);

        SettingsLoadResult result = await new JsonSettingsService(temp.SettingsPath).LoadAsync();

        ServiceInstance service = Assert.Single(result.Settings.Services);
        Assert.True(result.WasMigrated);
        Assert.Equal(id, service.Id);
        Assert.Equal(profileName, service.ProfileName);
        Assert.False(service.IsEnabled);
        Assert.True(service.IsMuted);
        Assert.Equal(4, service.SortOrder);
        Assert.Equal(NotificationPermissionState.Allowed, service.NotificationPermissionState);
        Assert.Equal(id, result.Settings.LastServiceId);
        Assert.False(result.Settings.CloseToTray);
        Assert.True(result.Settings.Notifications.DoNotDisturb);
        Assert.False(result.Settings.Notifications.ShowNotificationPreview);
        Assert.False(result.Settings.Notifications.PlaySound);
    }

    [Fact]
    public void PersistedSettings_DoNotContainNotificationContent()
    {
        AppSettings settings = AppSettings.CreateDefault();
        const string title = "Private title 5d9a";
        const string body = "Private body 71f2";
        WebNotificationRequest runtimeRequest = new(
            Guid.NewGuid(),
            "https://web.telegram.org/",
            title,
            body,
            new WebNotificationLifecycle(() => { }, () => { }, () => { }));

        string json = JsonSerializer.Serialize(settings);

        Assert.NotNull(runtimeRequest);
        Assert.DoesNotContain(title, json, StringComparison.Ordinal);
        Assert.DoesNotContain(body, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsPersistence_RemainsAtomic()
    {
        using TempSettingsFolder temp = new();
        JsonSettingsService service = new(temp.SettingsPath);
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance account = CreateService(ServiceType.Telegram);
        settings.Services.Add(account);
        await service.SaveAsync(settings);

        settings.CloseToTray = false;
        settings.Notifications.PlaySound = false;
        await service.SaveAsync(settings);

        SettingsLoadResult loaded = await service.LoadAsync();
        string[] temporaryFiles = Directory.GetFiles(temp.DirectoryPath, ".settings.json.*.tmp");

        Assert.False(loaded.Settings.CloseToTray);
        Assert.False(loaded.Settings.Notifications.PlaySound);
        Assert.Equal(account.Id, Assert.Single(loaded.Settings.Services).Id);
        Assert.Empty(temporaryFiles);
    }

    [Fact]
    public void SoundAbstraction_ReceivesWhatsAppInDefaultLanternMode()
    {
        ServiceInstance service = CreateService(ServiceType.WhatsApp);
        AppSettings settings = new() { Services = [service] };
        FakeSettingsStore store = new(settings);
        FakeNotificationSoundPlayer player = new();
        using TelegramNotificationSoundCoordinator coordinator = new(
            store,
            player,
            new ImmediateDispatcher(),
            TimeProvider.System);

        bool accepted = coordinator.RequestSound(
            new TelegramNotificationSoundRequest(service.Id, ServiceType.WhatsApp, "safe-hash"));

        Assert.True(settings.Notifications.PlaySound);
        Assert.True(accepted);
        Assert.Equal(ServiceType.WhatsApp, player.LastServiceType);
    }

    private SettingsFixture CreateFixture(bool includeSecondAccount = false)
    {
        AppSettings settings = AppSettings.CreateDefault();
        settings.Services.Add(CreateService(ServiceType.Telegram));
        if (includeSecondAccount)
        {
            ServiceInstance whatsapp = CreateService(ServiceType.WhatsApp);
            whatsapp.SortOrder = 1;
            settings.Services.Add(whatsapp);
        }

        return new SettingsFixture(settings, _catalog);
    }

    private ServiceInstance CreateService(ServiceType serviceType)
    {
        Guid id = Guid.NewGuid();
        ServiceDefinition definition = _catalog.Get(serviceType);
        return new ServiceInstance
        {
            Id = id,
            ServiceType = serviceType,
            DisplayName = definition.DisplayName,
            StartUrl = definition.StartUri?.AbsoluteUri,
            ProfileName = ProfileNameFactory.Create(id),
            IsEnabled = true,
            NotificationPermissionState = NotificationPermissionState.Allowed
        };
    }

    private static async Task DeleteThroughExistingLifecycleAsync(
        SettingsFixture fixture,
        ServiceInstance service)
    {
        bool profileDeleted = await fixture.Session.ClearProfileAsync(service);
        await fixture.Main.RemoveServiceAsync(service, profileDeleted);
    }

    private sealed class SettingsFixture : IDisposable
    {
        public SettingsFixture(AppSettings settings, BuiltInServiceCatalog catalog)
        {
            Settings = settings;
            Store = new FakeSettingsStore(settings);
            Session = new RecordingSessionManager();
            Notifications = new FakeWebNotificationCoordinator();
            Activity = new ServiceActivityCoordinator();
            Main = new MainWindowViewModel(
                catalog,
                Session,
                Store,
                Activity,
                Notifications);
            Main.Initialize(settings);
            Catalog = catalog;
        }

        public AppSettings Settings { get; }
        public BuiltInServiceCatalog Catalog { get; }
        public FakeSettingsStore Store { get; }
        public RecordingSessionManager Session { get; }
        public FakeWebNotificationCoordinator Notifications { get; }
        public ServiceActivityCoordinator Activity { get; }
        public MainWindowViewModel Main { get; }

        public SettingsViewModel CreateSettingsViewModel() => new(Main, Catalog);

        public void Dispose() => Main.Dispose();
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IApplicationSettingsStore
    {
        public AppSettings Current { get; private set; } = settings;
        public bool IsInitialized => true;
        public int SaveCount { get; private set; }

        public void Initialize(AppSettings value) => Current = value;

        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionManager : IWebViewSessionManager
    {
        private readonly HashSet<Guid> _sessions = [];

        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested { add { } remove { } }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged { add { } remove { } }
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived { add { } remove { } }
        public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived { add { } remove { } }

        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted { get; private set; }
        public int InitializedSessionCount => _sessions.Count;
        public int InitialNavigationCount => 0;
        public int InitializeCount { get; private set; }
        public int DeactivateCount { get; private set; }
        public int ReleaseSessionCount => ReleaseSessionRequests.Count;
        public int ClearProfileCount { get; private set; }
        public List<Guid> ReleaseSessionRequests { get; } = [];
        public ServiceInstance? LastInitializedService { get; private set; }
        public Guid? LastClearedServiceId { get; private set; }
        public string? LastClearedProfileName { get; private set; }

        public void AddSession(Guid serviceInstanceId) => _sessions.Add(serviceInstanceId);

        public Task<bool> InitializeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            LastInitializedService = serviceInstance;
            _sessions.Add(serviceInstance.Id);
            return Task.FromResult(true);
        }
        public Task<bool> PrimeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default)
        {
            LastInitializedService = serviceInstance;
            _sessions.Add(serviceInstance.Id);
            return Task.FromResult(true);
        }
        public bool IsSessionInitialized(Guid serviceInstanceId) => _sessions.Contains(serviceInstanceId);
        public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false) { }
        public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible) { }
        public void NotifyParentWindowPositionChanged() { }
        public bool HasSession(Guid serviceInstanceId) => _sessions.Contains(serviceInstanceId);
        public void DeactivateSession() => DeactivateCount++;
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId)
        {
            ReleaseSessionRequests.Add(serviceInstanceId);
            _sessions.Remove(serviceInstanceId);
        }

        public Task ReleaseSessionAsync(Guid serviceInstanceId, CancellationToken cancellationToken = default)
        {
            ReleaseSession(serviceInstanceId);
            return Task.CompletedTask;
        }

        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default)
        {
            ClearProfileCount++;
            LastClearedServiceId = serviceInstance.Id;
            LastClearedProfileName = serviceInstance.ProfileName;
            _sessions.Remove(serviceInstance.Id);
            return Task.FromResult(true);
        }

        public void ReleaseAllSessions() => _sessions.Clear();
        public void BeginShutdown() => IsShutdownStarted = true;
        public void Dispose() { }
    }

    private sealed class FakeWebNotificationCoordinator : IWebNotificationCoordinator
    {
        public int PendingCount => 0;
        public bool HasActiveNotification => false;
        public List<Guid> DiscardedServiceIds { get; } = [];

        public void Handle(WebNotificationRequest request) { }
        public void DiscardPending(Guid serviceInstanceId) => DiscardedServiceIds.Add(serviceInstanceId);
        public void OnDoNotDisturbChanged(bool enabled) { }
        public void Shutdown() { }
        public void Dispose() { }
    }

    private sealed class FakeNotificationPopupService : INotificationPopupService
    {
        event EventHandler<NotificationPopupEventArgs>? INotificationPopupService.Clicked
        {
            add { }
            remove { }
        }

        public event EventHandler<NotificationPopupEventArgs>? Closed;
        public List<NotificationPopupDisplayModel> Shown { get; } = [];
        public int VisibleCount => Shown.Count;

        public bool TryShow(NotificationPopupDisplayModel notification)
        {
            Shown.Add(notification);
            return true;
        }

        public void Close(Guid notificationId) => Closed?.Invoke(this, new NotificationPopupEventArgs(notificationId));
        public void CloseAll() { }
        public void Dispose() { }
    }

    private sealed class FakeNotificationSoundPlayer : INotificationSoundPlayer
    {
        public int PlayCount { get; private set; }
        public ServiceType? LastServiceType { get; private set; }
        public bool TryPlay(ServiceType serviceType)
        {
            PlayCount++;
            LastServiceType = serviceType;
            return true;
        }

        public bool TryPreviewLanternSound() => true;
    }

    private sealed class FakeTrayIconService : ITrayIconService
    {
        event EventHandler? ITrayIconService.OpenRequested { add { } remove { } }
        public event EventHandler? SettingsRequested;
        event EventHandler? ITrayIconService.DoNotDisturbToggleRequested { add { } remove { } }
        event EventHandler? ITrayIconService.ExitRequested { add { } remove { } }
        event EventHandler? ITrayIconService.BalloonClicked { add { } remove { } }
        event EventHandler? ITrayIconService.BalloonClosed { add { } remove { } }

        public void Show(bool doNotDisturb, string toolTipText) { }
        public void BeginShutdown() { }
        public void SetDoNotDisturb(bool enabled) { }
        public void SetToolTip(string text) { }
        public bool TryShowBalloon(string title, string text, int timeoutMilliseconds = 5000) => false;
        public void RaiseSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }

    private sealed class FakeWindowActivationService : IWindowActivationService
    {
        public bool IsMainWindowActive => false;
        public bool IsMainWindowVisible => false;
        public Guid? SelectedServiceId => null;
        public int ShowCount { get; private set; }

        public void Attach(System.Windows.Window window, Func<Guid?> selectedServiceId, Action<Guid> selectService) { }
        public void Detach(System.Windows.Window window) { }
        public void ShowAndActivate(Guid? serviceInstanceId = null) => ShowCount++;
    }

    private sealed class FakeTaskbarActivityIndicator : ITaskbarActivityIndicator
    {
        public bool HasActivity { get; private set; }
        public void Attach(System.Windows.Window window) { }
        public void Detach(System.Windows.Window window) { }
        public void SetHasActivity(bool hasActivity) => HasActivity = hasActivity;
        public void Dispose() { }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private sealed class TempSettingsFolder : IDisposable
    {
        public TempSettingsFolder()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        }

        public string DirectoryPath { get; }
        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
