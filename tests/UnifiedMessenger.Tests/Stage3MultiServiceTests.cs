using System.Drawing;
using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage3MultiServiceTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Fact]
    public void Add_CreatesTelegramAccount()
    {
        ServiceInstance account = Add(ServiceType.Telegram);

        AssertAccount(account, ServiceType.Telegram, "https://web.telegram.org/k/");
    }

    [Fact]
    public void Add_CreatesWhatsAppAccount()
    {
        ServiceInstance account = Add(ServiceType.WhatsApp);

        AssertAccount(account, ServiceType.WhatsApp, "https://web.whatsapp.com/");
    }

    [Fact]
    public void Add_CreatesMaxAccount()
    {
        ServiceInstance account = Add(ServiceType.Max);

        AssertAccount(account, ServiceType.Max, "https://web.max.ru/");
    }

    [Fact]
    public void Add_CreatesVkMessengerAccount()
    {
        ServiceInstance account = Add(ServiceType.VkMessenger);

        AssertAccount(account, ServiceType.VkMessenger, "https://web.vk.me/");
    }

    [Fact]
    public void Add_AllowsMultipleAccountsOfSameTypeWithUniqueProfiles()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceDefinition definition = _catalog.Get(ServiceType.WhatsApp);

        ServiceInstance personal = ServiceInstanceManager.Add(settings, definition, "WhatsApp Личный");
        ServiceInstance work = ServiceInstanceManager.Add(settings, definition, "WhatsApp Работа");

        Assert.NotEqual(personal.Id, work.Id);
        Assert.NotEqual(personal.ProfileName, work.ProfileName);
        Assert.Equal(2, settings.Services.Count);
    }

    [Fact]
    public async Task ProfileName_RemainsStableAfterSettingsRoundTrip()
    {
        using TempSettingsFolder temp = new();
        JsonSettingsService settingsService = new(temp.SettingsPath);
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance original = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.Telegram));

        await settingsService.SaveAsync(settings);
        SettingsLoadResult restored = await settingsService.LoadAsync();

        ServiceInstance account = Assert.Single(restored.Settings.Services);
        Assert.Equal(original.Id, account.Id);
        Assert.Equal(original.ProfileName, account.ProfileName);
    }

    [Fact]
    public async Task Migration_FromStage2PreservesTelegramIdAndProfile()
    {
        using TempSettingsFolder temp = new();
        Guid telegramId = Guid.Parse("08408e19-4485-4157-9091-44b05a44a0cd");
        string profileName = ProfileNameFactory.Create(telegramId);
        string stage2Json = $$"""
            {
              "schemaVersion": 1,
              "restoreLastService": true,
              "lastServiceId": "{{telegramId}}",
              "services": [
                {
                  "id": "{{telegramId}}",
                  "serviceType": "Telegram",
                  "displayName": "Telegram",
                  "startUrl": "https://web.telegram.org/k/",
                  "profileName": "{{profileName}}",
                  "isEnabled": true,
                  "sortOrder": 0
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(temp.SettingsPath, stage2Json);
        JsonSettingsService settingsService = new(temp.SettingsPath);

        SettingsLoadResult migrated = await settingsService.LoadAsync();

        Assert.True(migrated.WasMigrated);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.Settings.SchemaVersion);
        ServiceInstance telegram = Assert.Single(migrated.Settings.Services);
        Assert.Equal(telegramId, telegram.Id);
        Assert.Equal(profileName, telegram.ProfileName);
        Assert.Equal(telegramId, migrated.Settings.LastServiceId);
        Assert.Empty(migrated.Settings.PendingProfileDeletions);
    }

    [Fact]
    public void Initialize_RestoresLastSelectedAccount()
    {
        AppSettings settings = AppSettings.CreateDefault();
        _ = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.Telegram));
        ServiceInstance whatsapp = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.WhatsApp));
        settings.LastServiceId = whatsapp.Id;
        using MainWindowViewModel viewModel = new(
            _catalog,
            new StubSessionManager(),
            new ApplicationSettingsStore(new StubSettingsService()),
            new ServiceActivityCoordinator(),
            new StubWebNotificationCoordinator());

        viewModel.Initialize(settings);

        Assert.Equal(whatsapp.Id, viewModel.SelectedService?.Id);
    }

    [Fact]
    public void Rename_PreservesIdentityAndProfile()
    {
        ServiceInstance account = Add(ServiceType.Telegram);
        Guid id = account.Id;
        string profileName = account.ProfileName;

        ServiceInstanceManager.Rename(account, "Telegram Работа");

        Assert.Equal("Telegram Работа", account.DisplayName);
        Assert.Equal(id, account.Id);
        Assert.Equal(profileName, account.ProfileName);
    }

    [Fact]
    public void DisableAndEnable_PreservesIdentityAndProfile()
    {
        ServiceInstance account = Add(ServiceType.Max);
        Guid id = account.Id;
        string profileName = account.ProfileName;

        ServiceInstanceManager.SetEnabled(account, false);
        Assert.False(account.IsEnabled);
        ServiceInstanceManager.SetEnabled(account, true);

        Assert.True(account.IsEnabled);
        Assert.Equal(id, account.Id);
        Assert.Equal(profileName, account.ProfileName);
    }

    [Fact]
    public void Remove_DeletesOnlyRequestedAccount()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance telegram = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.Telegram));
        ServiceInstance whatsapp = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.WhatsApp));

        bool removed = ServiceInstanceManager.Remove(settings.Services, telegram.Id);

        Assert.True(removed);
        ServiceInstance remaining = Assert.Single(settings.Services);
        Assert.Equal(whatsapp.Id, remaining.Id);
        Assert.Equal(0, remaining.SortOrder);
    }

    [Fact]
    public async Task ProfileCleaner_DoesNotDeleteAnotherAccountsProfile()
    {
        using TempAppPaths paths = new();
        WebViewProfileCleaner cleaner = new(paths);
        Guid targetId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        string targetProfile = ProfileNameFactory.Create(targetId);
        string otherProfile = ProfileNameFactory.Create(otherId);
        string targetFolder = Directory.CreateDirectory(Path.Combine(paths.WebViewDataFolder, targetProfile)).FullName;
        string otherFolder = Directory.CreateDirectory(Path.Combine(paths.WebViewDataFolder, otherProfile)).FullName;
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "target.marker"), "test");
        await File.WriteAllTextAsync(Path.Combine(otherFolder, "other.marker"), "test");

        bool deleted = await cleaner.TryDeleteProfileAsync(targetProfile);

        Assert.True(deleted);
        Assert.False(Directory.Exists(targetFolder));
        Assert.True(File.Exists(Path.Combine(otherFolder, "other.marker")));
    }

    [Fact]
    public async Task PendingCleanup_NeverDeletesAProfileReferencedByAnAccount()
    {
        using TempAppPaths paths = new();
        WebViewProfileCleaner cleaner = new(paths);
        ServiceInstance telegram = Add(ServiceType.Telegram);
        string profileFolder = Directory.CreateDirectory(
            Path.Combine(paths.WebViewDataFolder, telegram.ProfileName)).FullName;
        await File.WriteAllTextAsync(Path.Combine(profileFolder, "keep.marker"), "test");
        AppSettings settings = new()
        {
            Services = [telegram],
            PendingProfileDeletions = [telegram.ProfileName]
        };

        bool changed = await cleaner.ProcessPendingDeletionsAsync(settings);

        Assert.True(changed);
        Assert.Empty(settings.PendingProfileDeletions);
        Assert.True(File.Exists(Path.Combine(profileFolder, "keep.marker")));
    }

    [Theory]
    [InlineData("https://web.whatsapp.com/", true)]
    [InlineData("https://static.web.whatsapp.com/app", true)]
    [InlineData("https://whatsapp.example.com/", false)]
    public void NavigationPolicy_UsesWhatsAppHosts(string address, bool expected)
    {
        NavigationPolicy policy = new(_catalog);

        Assert.Equal(expected, policy.IsAllowedTopLevelNavigation(ServiceType.WhatsApp, new Uri(address)));
    }

    [Theory]
    [InlineData("https://web.max.ru/", true)]
    [InlineData("https://id.max.ru/login", true)]
    [InlineData("https://max.ru.evil.example/", false)]
    public void NavigationPolicy_UsesMaxHosts(string address, bool expected)
    {
        NavigationPolicy policy = new(_catalog);

        Assert.Equal(expected, policy.IsAllowedTopLevelNavigation(ServiceType.Max, new Uri(address)));
    }

    [Theory]
    [InlineData("https://web.vk.me/", true)]
    [InlineData("https://id.vk.com/auth", true)]
    [InlineData("https://vk.example.com/", false)]
    public void NavigationPolicy_UsesVkHosts(string address, bool expected)
    {
        NavigationPolicy policy = new(_catalog);

        Assert.Equal(expected, policy.IsAllowedTopLevelNavigation(ServiceType.VkMessenger, new Uri(address)));
    }

    [Fact]
    public void NavigationService_OpensExternalDomainInSystemBrowserService()
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = new(
            new NavigationPolicy(_catalog),
            browser,
            new ExternalBrowserLaunchPolicy());
        Uri externalUri = new("https://example.com/help");

        WebNavigationDisposition disposition = navigation.Route(
            ServiceType.Telegram,
            externalUri,
            isUserInitiated: true);

        Assert.Equal(WebNavigationDisposition.ExternalOpened, disposition);
        Assert.Equal(externalUri, browser.LastOpenedUri);
    }

    [Fact]
    public void Move_ChangesAndNormalizesAccountOrder()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance telegram = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.Telegram));
        ServiceInstance whatsapp = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.WhatsApp));
        ServiceInstance max = ServiceInstanceManager.Add(settings, _catalog.Get(ServiceType.Max));

        bool moved = ServiceInstanceManager.Move(settings.Services, max.Id, -1);

        Assert.True(moved);
        Assert.Equal(
            [telegram.Id, max.Id, whatsapp.Id],
            ServiceInstanceManager.Sort(settings.Services).Select(service => service.Id));
        Assert.Equal([0, 2, 1], settings.Services.Select(service => service.SortOrder));
    }

    private ServiceInstance Add(ServiceType serviceType)
    {
        AppSettings settings = AppSettings.CreateDefault();
        return ServiceInstanceManager.Add(settings, _catalog.Get(serviceType));
    }

    private static void AssertAccount(ServiceInstance account, ServiceType serviceType, string startUrl)
    {
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal(serviceType, account.ServiceType);
        Assert.Equal(startUrl, account.StartUrl);
        Assert.Equal(ProfileNameFactory.Create(account.Id), account.ProfileName);
        Assert.True(account.IsEnabled);
    }

    private sealed class RecordingExternalBrowserService : IExternalBrowserService
    {
        public Uri? LastOpenedUri { get; private set; }

        public bool TryOpen(Uri uri)
        {
            LastOpenedUri = uri;
            return true;
        }
    }

    private sealed class StubSessionManager : IWebViewSessionManager
    {
        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived
        {
            add { }
            remove { }
        }

        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted { get; private set; }
        public int InitializedSessionCount => 0;
        public int InitialNavigationCount => 0;
        public Task<bool> InitializeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> PrimeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public bool IsSessionInitialized(Guid serviceInstanceId) => false;
        public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false) { }
        public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible) { }
        public void NotifyParentWindowPositionChanged() { }
        public bool HasSession(Guid serviceInstanceId) => false;
        public void DeactivateSession() { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId) { }
        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public void ReleaseAllSessions() { }
        public void BeginShutdown() => IsShutdownStarted = true;
        public void Dispose() { }
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(AppSettings.CreateDefault()));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubWebNotificationCoordinator : IWebNotificationCoordinator
    {
        public int PendingCount => 0;
        public bool HasActiveNotification => false;
        public void Handle(WebNotificationRequest request) { }
        public void DiscardPending(Guid serviceInstanceId) { }
        public void OnDoNotDisturbChanged(bool enabled) { }
        public void Shutdown() { }
        public void Dispose() { }
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

    private sealed class TempAppPaths : IAppPaths, IDisposable
    {
        public TempAppPaths()
        {
            LocalDataFolder = Path.Combine(Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            RoamingDataFolder = Path.Combine(LocalDataFolder, "Roaming");
            WebViewDataFolder = Path.Combine(LocalDataFolder, "WebView2");
            Directory.CreateDirectory(WebViewDataFolder);
        }

        public string RoamingDataFolder { get; }
        public string LocalDataFolder { get; }
        public string SettingsFilePath => Path.Combine(RoamingDataFolder, "settings.json");
        public string WebViewDataFolder { get; }
        public string LogsFolder => Path.Combine(LocalDataFolder, "Logs");

        public void Dispose()
        {
            if (Directory.Exists(LocalDataFolder))
            {
                Directory.Delete(LocalDataFolder, recursive: true);
            }
        }
    }
}
