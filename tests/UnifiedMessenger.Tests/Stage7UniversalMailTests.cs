using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

public sealed class Stage7UniversalMailTests
{
    [Fact]
    public void MailAccount_IsIndependentFromWebServiceInstance()
    {
        Assert.False(typeof(ServiceInstance).IsAssignableFrom(typeof(MailAccount)));
        Assert.Null(typeof(MailAccount).GetProperty(nameof(ServiceInstance.ProfileName)));
        Assert.Null(typeof(MailAccount).GetProperty("UserDataFolder"));
    }

    [Fact]
    public void MultipleMailAccounts_KeepIndependentIdsAndCredentialKeys()
    {
        AppSettings settings = new()
        {
            MailAccounts =
            [
                CreateMailAccount(MailProviderType.Yandex, "one@yandex.ru"),
                CreateMailAccount(MailProviderType.MailRu, "two@mail.ru")
            ]
        };

        Assert.Equal(2, settings.MailAccounts.Count);
        Assert.Equal(2, settings.MailAccounts.Select(account => account.Id).Distinct().Count());
        Assert.Equal(2, settings.MailAccounts.Select(account => account.CredentialKey).Distinct().Count());
    }

    [Fact]
    public void ProviderFactory_SelectsEveryProvider()
    {
        RecordingValidator validator = new();
        IMailProvider[] providers =
        [
            new GmailApiProvider(),
            new YandexMailProvider(validator),
            new MailRuMailProvider(validator),
            new GenericImapMailProvider(validator)
        ];
        MailProviderFactory factory = new(providers);

        foreach (MailProviderType providerType in Enum.GetValues<MailProviderType>())
        {
            Assert.Equal(providerType, factory.Get(providerType).ProviderType);
        }
    }

    [Fact]
    public async Task YandexPreset_UsesOfficialTlsEndpoints()
    {
        RecordingValidator validator = new();
        YandexMailProvider provider = new(validator);

        await provider.ValidateAsync(
            new MailAccountConnectionRequest(MailProviderType.Yandex, "user@yandex.ru", null),
            "app-secret");

        AssertServer(validator.Settings!.Imap, "imap.yandex.com", 993, "user@yandex.ru");
        AssertServer(validator.Settings.Smtp, "smtp.yandex.com", 465, "user@yandex.ru");
    }

    [Fact]
    public async Task MailRuPreset_UsesOfficialTlsEndpoints()
    {
        RecordingValidator validator = new();
        MailRuMailProvider provider = new(validator);

        await provider.ValidateAsync(
            new MailAccountConnectionRequest(MailProviderType.MailRu, "user@mail.ru", null),
            "app-secret");

        AssertServer(validator.Settings!.Imap, "imap.mail.ru", 993, "user@mail.ru");
        AssertServer(validator.Settings.Smtp, "smtp.mail.ru", 465, "user@mail.ru");
    }

    [Fact]
    public async Task GenericProvider_HonorsExplicitServerSettings()
    {
        RecordingValidator validator = new();
        GenericImapMailProvider provider = new(validator);
        MailConnectionSettings settings = new()
        {
            Imap = new MailServerSettings
            {
                Host = "imap.example.test",
                Port = 143,
                SecureSocketMode = MailSecureSocketMode.StartTls,
                Username = "imap-user"
            },
            Smtp = new MailServerSettings
            {
                Host = "smtp.example.test",
                Port = 587,
                SecureSocketMode = MailSecureSocketMode.StartTls,
                Username = "smtp-user"
            }
        };

        await provider.ValidateAsync(
            new MailAccountConnectionRequest(MailProviderType.GenericImap, "mail@example.test", null, settings),
            "secret");

        Assert.Equal("imap.example.test", validator.Settings!.Imap.Host);
        Assert.Equal(143, validator.Settings.Imap.Port);
        Assert.Equal(MailSecureSocketMode.StartTls, validator.Settings.Imap.SecureSocketMode);
        Assert.Equal("smtp-user", validator.Settings.Smtp.Username);
    }

    [Fact]
    public async Task CredentialStore_SaveLoadReplaceDelete_UsesSeparateKeys()
    {
        using TempFolder temp = new();
        FileMailCredentialStore store = new(temp.Path, new ReversibleProtector());
        string firstKey = Guid.NewGuid().ToString("N");
        string secondKey = Guid.NewGuid().ToString("N");

        await store.SaveAsync(firstKey, MailCredential.CreatePassword("first-secret"));
        await store.SaveAsync(secondKey, MailCredential.CreatePassword("second-secret"));
        await store.SaveAsync(firstKey, MailCredential.CreatePassword("replacement-secret"));

        Assert.Equal("replacement-secret", (await store.LoadAsync(firstKey))?.Secret);
        Assert.Equal("second-secret", (await store.LoadAsync(secondKey))?.Secret);
        await store.DeleteAsync(firstKey);
        Assert.Null(await store.LoadAsync(firstKey));
        Assert.Equal("second-secret", (await store.LoadAsync(secondKey))?.Secret);
    }

    [Fact]
    public async Task CredentialStore_CorruptedProtectedCredential_IsHandledSafely()
    {
        using TempFolder temp = new();
        string key = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp.Path);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(temp.Path, key + ".credential.bin"), [1, 2, 3]);
        FileMailCredentialStore store = new(temp.Path, new CorruptProtector());

        Assert.Null(await store.LoadAsync(key));
    }

    [Fact]
    public void DpapiProtector_UsesCurrentUserProtectedRoundTrip()
    {
        DpapiMailCredentialProtector protector = new();
        byte[] plaintext = Encoding.UTF8.GetBytes("mail-secret-for-test");
        byte[] encrypted = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, encrypted);
        Assert.Equal(plaintext, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task SettingsSerialization_ContainsMetadataButNoCredentialSecret()
    {
        using TempFolder temp = new();
        string settingsPath = System.IO.Path.Combine(temp.Path, "settings.json");
        AppSettings settings = new() { MailAccounts = [CreateMailAccount(MailProviderType.Yandex, "user@yandex.ru")] };

        await new JsonSettingsService(settingsPath).SaveAsync(settings);
        string json = await File.ReadAllTextAsync(settingsPath);

        Assert.Contains("user@yandex.ru", json, StringComparison.Ordinal);
        Assert.Contains("credentialKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"secret\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema3Migration_PreservesWebProfilesSelectionAndSettings()
    {
        using TempFolder temp = new();
        Guid telegramId = Guid.NewGuid();
        Guid vkId = Guid.NewGuid();
        string telegramProfile = ProfileNameFactory.Create(telegramId);
        string vkProfile = ProfileNameFactory.Create(vkId);
        string json = $$"""
            {
              "schemaVersion": 3,
              "lastServiceId": "{{vkId}}",
              "closeToTray": false,
              "notifications": {
                "isEnabled": true,
                "showNotificationPreview": false,
                "showServiceName": true,
                "playSound": false,
                "doNotDisturb": true
              },
              "services": [
                { "id": "{{telegramId}}", "serviceType": "Telegram", "displayName": "Telegram", "profileName": "{{telegramProfile}}", "isEnabled": true, "sortOrder": 0 },
                { "id": "{{vkId}}", "serviceType": "VkMessenger", "displayName": "VK", "profileName": "{{vkProfile}}", "isEnabled": true, "sortOrder": 1 }
              ]
            }
            """;
        string path = System.IO.Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, json);

        SettingsLoadResult result = await new JsonSettingsService(path).LoadAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(vkId, result.Settings.LastServiceId);
        Assert.Equal(vkId, result.Settings.LastNavigationAccountId);
        Assert.Equal([telegramId, vkId], result.Settings.Services.Select(service => service.Id));
        Assert.Equal([telegramProfile, vkProfile], result.Settings.Services.Select(service => service.ProfileName));
        Assert.Equal([0, 1], result.Settings.Services.Select(service => service.SortOrder));
        Assert.False(result.Settings.CloseToTray);
        Assert.False(result.Settings.Notifications.ShowNotificationPreview);
        Assert.False(result.Settings.Notifications.PlaySound);
        Assert.True(result.Settings.Notifications.DoNotDisturb);
    }

    [Fact]
    public async Task MailMetadata_SurvivesSettingsRestart()
    {
        using TempFolder temp = new();
        string path = System.IO.Path.Combine(temp.Path, "settings.json");
        MailAccount expected = CreateMailAccount(MailProviderType.GenericImap, "user@example.test");
        expected.GenericConnectionSettings = new MailConnectionSettings
        {
            Imap = new MailServerSettings { Host = "imap.example.test", Port = 993, Username = "u" },
            Smtp = new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "u" }
        };
        JsonSettingsService service = new(path);
        await service.SaveAsync(new AppSettings { MailAccounts = [expected], LastNavigationAccountId = expected.Id });

        SettingsLoadResult result = await service.LoadAsync();

        MailAccount actual = Assert.Single(result.Settings.MailAccounts);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.CredentialKey, actual.CredentialKey);
        Assert.Equal("imap.example.test", actual.GenericConnectionSettings!.Imap.Host);
        Assert.Equal(expected.Id, result.Settings.LastNavigationAccountId);
    }

    [Theory]
    [InlineData(MailConnectionFailureKind.AuthenticationFailed)]
    [InlineData(MailConnectionFailureKind.SmtpValidationFailed)]
    public async Task FailedValidation_DoesNotCreateMetadataOrCredential(MailConnectionFailureKind failureKind)
    {
        AppSettings settings = new();
        RecordingSettingsStore settingsStore = new(settings);
        RecordingCredentialStore credentials = new();
        StaticProvider provider = new(
            MailProviderType.Yandex,
            MailConnectionValidationResult.Failure(failureKind, "Безопасная ошибка подключения."));
        MailAccountProvisioningService service = CreateProvisioner(provider, credentials, settingsStore);

        MailAccountProvisioningResult result = await service.ConnectAsync(
            new MailAccountConnectionRequest(MailProviderType.Yandex, "user@yandex.ru", null),
            "highly-secret-value");

        Assert.False(result.IsSuccess);
        Assert.Empty(settings.MailAccounts);
        Assert.Empty(credentials.Values);
        Assert.DoesNotContain("highly-secret-value", result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulValidation_CreatesAccountAndCredentialExactlyOnce()
    {
        AppSettings settings = new();
        RecordingSettingsStore settingsStore = new(settings);
        RecordingCredentialStore credentials = new();
        StaticProvider provider = new(
            MailProviderType.MailRu,
            MailConnectionValidationResult.Success(new MailIdentity("user@mail.ru", null)));
        MailAccountProvisioningService service = CreateProvisioner(provider, credentials, settingsStore);

        MailAccountProvisioningResult result = await service.ConnectAsync(
            new MailAccountConnectionRequest(MailProviderType.MailRu, "user@mail.ru", "Работа"),
            "secret");

        Assert.True(result.IsSuccess);
        Assert.Single(settings.MailAccounts);
        Assert.Single(credentials.Values);
        Assert.Equal(1, settingsStore.SaveCount);
        Assert.Equal(result.Account!.CredentialKey, credentials.Values.Single().Key);
    }

    [Fact]
    public async Task Delete_RemovesOnlyMailMetadataAndCredential()
    {
        ServiceInstance web = new()
        {
            Id = Guid.NewGuid(),
            ServiceType = ServiceType.Telegram,
            DisplayName = "Telegram",
            ProfileName = "service-existing",
            IsEnabled = true
        };
        MailAccount first = CreateMailAccount(MailProviderType.Yandex, "one@yandex.ru");
        MailAccount second = CreateMailAccount(MailProviderType.MailRu, "two@mail.ru");
        AppSettings settings = new() { Services = [web], MailAccounts = [first, second] };
        RecordingCredentialStore credentials = new();
        credentials.Values[first.CredentialKey] = MailCredential.CreatePassword("one");
        credentials.Values[second.CredentialKey] = MailCredential.CreatePassword("two");
        MailAccountProvisioningService service = CreateProvisioner(
            new StaticProvider(MailProviderType.Yandex, MailConnectionValidationResult.Success(new MailIdentity("x", null))),
            credentials,
            new RecordingSettingsStore(settings));

        await service.DeleteAsync(first.Id);

        Assert.Equal(second.Id, Assert.Single(settings.MailAccounts).Id);
        Assert.DoesNotContain(first.CredentialKey, credentials.Values.Keys);
        Assert.Contains(second.CredentialKey, credentials.Values.Keys);
        Assert.Equal(web.Id, Assert.Single(settings.Services).Id);
        Assert.Equal("service-existing", web.ProfileName);
    }

    [Fact]
    public void Navigation_ShowsMailBesideWebAndMailSelectionCreatesNoWebSession()
    {
        ServiceInstance telegram = new()
        {
            Id = Guid.NewGuid(),
            ServiceType = ServiceType.Telegram,
            DisplayName = "Telegram",
            ProfileName = ProfileNameFactory.Create(Guid.NewGuid()),
            IsEnabled = true
        };
        MailAccount mail = CreateMailAccount(MailProviderType.Yandex, "user@yandex.ru");
        AppSettings settings = new() { Services = [telegram], MailAccounts = [mail], LastServiceId = telegram.Id };
        RecordingSessionManager sessions = new();
        using MainWindowViewModel viewModel = CreateMainViewModel(settings, sessions);

        Assert.Equal([telegram.Id, mail.Id], viewModel.NavigationItems.Select(item => item.Id));
        viewModel.SelectMailAccount(mail.Id);

        Assert.Same(mail, viewModel.SelectedMailAccount);
        Assert.Null(viewModel.SelectedService);
        Assert.Equal(0, sessions.InitializeCount);
        Assert.True(viewModel.IsMailSelected);

        viewModel.SelectService(telegram.Id);
        Assert.Same(telegram, viewModel.SelectedService);
        Assert.Null(viewModel.SelectedMailAccount);
        Assert.True(viewModel.HasActiveWebView);
    }

    [Theory]
    [InlineData(ServiceType.Telegram)]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    [InlineData(ServiceType.VkMessenger)]
    public async Task RestartWithMailSelected_ExistingWebControllerNavigationSucceedsWithoutErrorFeedback(
        ServiceType serviceType)
    {
        ServiceInstance web = new()
        {
            Id = Guid.NewGuid(),
            ServiceType = serviceType,
            DisplayName = serviceType.ToString(),
            ProfileName = ProfileNameFactory.Create(Guid.NewGuid()),
            IsEnabled = true
        };
        MailAccount mail = CreateMailAccount(MailProviderType.Yandex, "user@yandex.ru");
        AppSettings settings = new()
        {
            Services = [web],
            MailAccounts = [mail],
            LastServiceId = web.Id,
            LastNavigationAccountId = mail.Id
        };
        RecordingSessionManager sessions = new();
        using MainWindowViewModel viewModel = CreateMainViewModel(settings, sessions);
        Assert.Same(mail, viewModel.SelectedMailAccount);

        int errorNotifications = 0;
        int errorSounds = 0;
        bool layoutReady = false;
        bool initialized = false;
        try
        {
            viewModel.SelectService(web.Id);
            Rectangle bounds = await WebViewSurfaceBoundsReadiness.GetReadyBoundsAsync(
                () => layoutReady,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    layoutReady = true;
                    return Task.CompletedTask;
                },
                () => layoutReady ? new Rectangle(0, 0, 1200, 700) : Rectangle.Empty,
                CancellationToken.None);
            initialized = await sessions.InitializeAsync(
                new IntPtr(1),
                bounds,
                viewModel.SelectedService!,
                activate: true);
            if (!initialized)
            {
                errorNotifications++;
                errorSounds++;
            }
        }
        catch
        {
            errorNotifications++;
            errorSounds++;
        }

        Assert.True(initialized);
        Assert.Equal(0, errorNotifications);
        Assert.Equal(0, errorSounds);
        Assert.Same(web, viewModel.SelectedService);
        Assert.Null(viewModel.SelectedMailAccount);
        Assert.Equal(web.Id, sessions.LastInitializedServiceId);
        Assert.True(sessions.LastInitializeActivated);
        Assert.True(sessions.LastBounds.Width > 0);
        Assert.Equal(0, sessions.InitialNavigationCount);
        Assert.Equal(web.Id, viewModel.CreateSettingsSnapshot().LastNavigationAccountId);
        Assert.Equal(web.ProfileName, viewModel.SelectedService!.ProfileName);
    }

    [Fact]
    public void Navigation_WebMailAndMailMailTransitionsDoNotInitializeWebSessions()
    {
        ServiceInstance web = new()
        {
            Id = Guid.NewGuid(),
            ServiceType = ServiceType.Telegram,
            DisplayName = "Telegram",
            ProfileName = ProfileNameFactory.Create(Guid.NewGuid()),
            IsEnabled = true
        };
        MailAccount first = CreateMailAccount(MailProviderType.Yandex, "first@yandex.ru");
        MailAccount second = CreateMailAccount(MailProviderType.MailRu, "second@mail.ru");
        AppSettings settings = new()
        {
            Services = [web],
            MailAccounts = [first, second],
            LastNavigationAccountId = web.Id
        };
        RecordingSessionManager sessions = new();
        using MainWindowViewModel viewModel = CreateMainViewModel(settings, sessions);

        viewModel.SelectMailAccount(first.Id);
        Assert.Same(first, viewModel.SelectedMailAccount);
        viewModel.SelectMailAccount(second.Id);
        Assert.Same(second, viewModel.SelectedMailAccount);
        Assert.Null(viewModel.SelectedService);
        Assert.Equal(0, sessions.InitializeCount);

        viewModel.SelectService(web.Id);
        Assert.Same(web, viewModel.SelectedService);
        Assert.Equal(0, sessions.InitializeCount);
    }

    [Fact]
    public async Task GmailPlaceholder_UsesOAuthAndNeverRequestsWebViewOrPasswordValidation()
    {
        GmailApiProvider provider = new();

        MailConnectionValidationResult result = await provider.ValidateAsync(
            new MailAccountConnectionRequest(MailProviderType.Gmail, "user@gmail.com", null),
            string.Empty);

        Assert.Equal(MailAuthenticationKind.OAuth, provider.Descriptor.AuthenticationKind);
        Assert.Equal(MailConnectionFailureKind.OAuthNotAvailable, result.FailureKind);
        Assert.DoesNotContain("WebView", provider.Descriptor.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMailWindow_CanBeCreatedOnStaThread()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                RecordingValidator validator = new();
                MailProviderFactory factory = new(
                [
                    new GmailApiProvider(),
                    new YandexMailProvider(validator),
                    new MailRuMailProvider(validator),
                    new GenericImapMailProvider(validator)
                ]);
                AddMailAccountWindow window = new(factory, new NoOpProvisioningService());
                Assert.Equal(4, window.Providers.Count);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    private static MailAccountProvisioningService CreateProvisioner(
        IMailProvider provider,
        IMailCredentialStore credentials,
        IApplicationSettingsStore settingsStore) =>
        new(
            new MailProviderFactory([provider]),
            credentials,
            new NoOpGmailOAuthService(),
            settingsStore,
            TimeProvider.System);

    private static MainWindowViewModel CreateMainViewModel(
        AppSettings settings,
        RecordingSessionManager sessions)
    {
        RecordingSettingsStore store = new(settings);
        MainWindowViewModel viewModel = new(
            new BuiltInServiceCatalog(),
            sessions,
            store,
            new NoOpActivityCoordinator(),
            new NoOpNotificationCoordinator());
        viewModel.Initialize(settings);
        return viewModel;
    }

    private static MailAccount CreateMailAccount(MailProviderType provider, string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EmailAddress = email,
            CredentialKey = Guid.NewGuid().ToString("N"),
            IsEnabled = true
        };

    private static void AssertServer(MailServerSettings settings, string host, int port, string username)
    {
        Assert.Equal(host, settings.Host);
        Assert.Equal(port, settings.Port);
        Assert.Equal(username, settings.Username);
        Assert.Equal(MailSecureSocketMode.SslOnConnect, settings.SecureSocketMode);
    }

    private sealed class RecordingValidator : IMailConnectionValidator
    {
        public MailConnectionSettings? Settings { get; private set; }

        public Task<MailConnectionValidationResult> ValidateAsync(
            MailConnectionSettings settings,
            string emailAddress,
            string secret,
            CancellationToken cancellationToken = default)
        {
            Settings = settings.Clone();
            return Task.FromResult(MailConnectionValidationResult.Success(new MailIdentity(emailAddress, null)));
        }
    }

    private sealed class StaticProvider(
        MailProviderType providerType,
        MailConnectionValidationResult result) : IMailProvider
    {
        public MailProviderType ProviderType => providerType;
        public MailProviderDescriptor Descriptor { get; } = new(
            providerType,
            providerType.ToString(),
            providerType == MailProviderType.Gmail ? MailAuthenticationKind.OAuth : MailAuthenticationKind.Password,
            MailProviderCapabilities.ConnectionValidation,
            string.Empty);

        public Task<MailConnectionValidationResult> ValidateAsync(
            MailAccountConnectionRequest request,
            string secret,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class RecordingCredentialStore : IMailCredentialStore
    {
        public Dictionary<string, MailCredential> Values { get; } = [];

        public Task SaveAsync(
            string credentialKey,
            MailCredential credential,
            CancellationToken cancellationToken = default)
        {
            Values[credentialKey] = credential;
            return Task.CompletedTask;
        }

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(credentialKey, out MailCredential? value) ? value : null);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
        {
            Values.Remove(credentialKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettingsStore(AppSettings settings) : IApplicationSettingsStore
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

    private sealed class ReversibleProtector : IMailCredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Reverse().ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray().Reverse().ToArray();
    }

    private sealed class CorruptProtector : IMailCredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => throw new CryptographicException();
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class RecordingSessionManager : IWebViewSessionManager
    {
        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested { add { } remove { } }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged { add { } remove { } }
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived { add { } remove { } }
        public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived { add { } remove { } }
        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted => false;
        public int InitializedSessionCount => 0;
        public int InitialNavigationCount => 0;
        public int InitializeCount { get; private set; }
        public Guid? LastInitializedServiceId { get; private set; }
        public bool LastInitializeActivated { get; private set; }
        public Rectangle LastBounds { get; private set; }
        public Task<bool> InitializeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            LastInitializedServiceId = serviceInstance.Id;
            LastInitializeActivated = activate;
            LastBounds = bounds;
            return Task.FromResult(true);
        }
        public Task<bool> PrimeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(true);
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
        public Task ReleaseSessionAsync(Guid serviceInstanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void ReleaseAllSessions() { }
        public void BeginShutdown() { }
        public void Dispose() { }
    }

    private sealed class NoOpActivityCoordinator : IServiceActivityCoordinator
    {
        public event EventHandler? ActivityChanged { add { } remove { } }
        public void UpdateFromDocumentTitle(ServiceInstance service, string? documentTitle, bool markActivity) { }
        public void MarkNotificationReceived(ServiceInstance service) { }
        public void Clear(ServiceInstance service) { }
        public void Reset(IEnumerable<ServiceInstance> services) { }
        public string CreateTrayToolTip(IEnumerable<ServiceInstance> services) => string.Empty;
    }

    private sealed class NoOpNotificationCoordinator : IWebNotificationCoordinator
    {
        public int PendingCount => 0;
        public bool HasActiveNotification => false;
        public void Handle(WebNotificationRequest request) { }
        public void DiscardPending(Guid serviceInstanceId) { }
        public void OnDoNotDisturbChanged(bool enabled) { }
        public void Shutdown() { }
        public void Dispose() { }
    }

    private sealed class NoOpProvisioningService : IMailAccountProvisioningService
    {
        public Task<MailAccountProvisioningResult> ConnectAsync(
            MailAccountConnectionRequest request,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.OAuthNotAvailable,
                    "Недоступно."));

        public Task<MailAccountProvisioningResult> ConnectGmailAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.OAuthNotAvailable,
                    "Недоступно."));

        public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpGmailOAuthService : IGmailOAuthService
    {
        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthNotAvailable,
                    "Недоступно."));

        public Task<GmailProfileResult> GetProfileAsync(
            GmailOAuthSession session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(GmailProfileResult.Failure("Недоступно."));
    }
}
