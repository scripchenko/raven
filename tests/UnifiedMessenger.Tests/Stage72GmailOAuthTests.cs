using System.Net;
using System.Net.Http;
using System.Text;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage72GmailOAuthTests
{
    [Fact]
    public async Task GmailOAuth_LaunchesOnlySystemBrowser()
    {
        OAuthFixture fixture = new();

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(Uri.UriSchemeHttps, fixture.Browser.LastUri!.Scheme);
        Assert.Equal("accounts.google.com", fixture.Browser.LastUri.Host);
        Assert.DoesNotContain(
            typeof(GmailOAuthService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("WebView", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loopback_UsesIpv4AndTwoConcurrentListenersGetDifferentDynamicPorts()
    {
        OAuthLoopbackListenerFactory factory = new();
        await using IOAuthLoopbackListener first = factory.Create();
        await using IOAuthLoopbackListener second = factory.Create();

        Assert.Equal("127.0.0.1", first.RedirectUri.Host);
        Assert.Equal("127.0.0.1", second.RedirectUri.Host);
        Assert.True(first.RedirectUri.Port > 0);
        Assert.True(second.RedirectUri.Port > 0);
        Assert.NotEqual(first.RedirectUri.Port, second.RedirectUri.Port);
        Assert.Equal(GmailOAuthConstants.CallbackPath, first.RedirectUri.AbsolutePath);
    }

    [Fact]
    public async Task Loopback_ReceivesOneCallbackAndReturnsContentFreeCompletionPage()
    {
        await using IOAuthLoopbackListener listener = new OAuthLoopbackListenerFactory().Create();
        Task<OAuthLoopbackResponse> callbackTask = listener.WaitForCallbackAsync();
        using HttpClient client = new(new HttpClientHandler { UseProxy = false });
        Uri callbackUri = new(listener.RedirectUri, "?code=test-code&state=test-state");

        string page = await client.GetStringAsync(callbackUri);
        OAuthLoopbackResponse response = await callbackTask;

        Assert.Equal("test-code", response.Code);
        Assert.Equal("test-state", response.State);
        Assert.Contains("Можно вернуться в Lantern", page, StringComparison.Ordinal);
        Assert.DoesNotContain("test-code", page, StringComparison.Ordinal);
        Assert.DoesNotContain("test-state", page, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthState_IsCryptographicallySizedAndChangesEveryTime()
    {
        CryptographicOAuthStateGenerator generator = new();

        string first = generator.CreateState();
        string second = generator.CreateState();

        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 40);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void OfficialGoogleClientBuildsPkceOfflineReadOnlyRequest()
    {
        GoogleOAuthProtocolClient client = new();

        GoogleOAuthAuthorizationRequest request = client.CreateAuthorizationRequest(
            new GoogleOAuthClientConfiguration("client-id-test", "client-secret-test"),
            new Uri("http://127.0.0.1:54321/oauth2/callback/"),
            "expected-state");
        IReadOnlyDictionary<string, string> query = ParseQuery(request.AuthorizationUri.Query);

        Assert.Equal(Uri.UriSchemeHttps, request.AuthorizationUri.Scheme);
        Assert.Equal("accounts.google.com", request.AuthorizationUri.Host);
        Assert.Equal("expected-state", query["state"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(request.CodeVerifier));
        Assert.NotEqual(request.CodeVerifier, query["code_challenge"]);
        Assert.Equal(GmailOAuthConstants.ReadOnlyScope, query["scope"]);
    }

    [Fact]
    public async Task OAuth_WrongStateIsRejectedBeforeTokenExchange()
    {
        OAuthFixture fixture = new(callback: new OAuthLoopbackResponse("code", "wrong-state", null));

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthStateMismatch, result.FailureKind);
        Assert.Equal(0, fixture.Protocol.ExchangeCount);
    }

    [Fact]
    public async Task OAuth_CancellationClosesThePendingTransaction()
    {
        OAuthFixture fixture = OAuthFixture.WithPendingCallback(TimeSpan.FromMinutes(1));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(40));

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync(cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OperationCanceled, result.FailureKind);
        Assert.True(fixture.Listener.IsDisposed);
    }

    [Fact]
    public async Task OAuth_TimeoutIsBoundedAndReported()
    {
        OAuthFixture fixture = OAuthFixture.WithPendingCallback(TimeSpan.FromMilliseconds(35));

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthTimeout, result.FailureKind);
        Assert.True(fixture.Listener.IsDisposed);
    }

    [Fact]
    public async Task OAuth_DenialDoesNotExchangeCode()
    {
        OAuthFixture fixture = new(callback: new OAuthLoopbackResponse(null, "expected-state", "access_denied"));

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthDenied, result.FailureKind);
        Assert.Equal(0, fixture.Protocol.ExchangeCount);
    }

    [Fact]
    public async Task OAuth_DenialWithWrongStateIsRejectedAsStateMismatch()
    {
        OAuthFixture fixture = new(callback: new OAuthLoopbackResponse(null, "wrong-state", "access_denied"));

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthStateMismatch, result.FailureKind);
        Assert.Equal(0, fixture.Protocol.ExchangeCount);
    }

    [Fact]
    public async Task OAuth_TokenExchangeErrorIsSanitized()
    {
        OAuthFixture fixture = new();
        fixture.Protocol.ExchangeException = new GoogleOAuthProtocolException("raw-token-exchange-detail");

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthTokenExchangeFailed, result.FailureKind);
        Assert.DoesNotContain("raw-token-exchange-detail", result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_MissingRefreshTokenIsRejected()
    {
        OAuthFixture fixture = new();
        fixture.Protocol.TokenResult = new GoogleOAuthTokenResult("access-token", string.Empty);

        GmailOAuthAuthorizationResult result = await fixture.Service.AuthorizeAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.OAuthRefreshTokenMissing, result.FailureKind);
    }

    [Fact]
    public async Task GmailConnection_StoresOnlyTypedRefreshCredentialAndCreatesProfileIdentity()
    {
        AppSettings settings = new();
        RecordingCredentialStore credentials = new();
        QueueGmailOAuthService oauth = new();
        oauth.Enqueue("profile-user@gmail.test", "refresh-token-one", "access-token-one");
        MailAccountProvisioningService service = CreateProvisioner(settings, credentials, oauth);

        MailAccountProvisioningResult result = await service.ConnectGmailAsync();

        Assert.True(result.IsSuccess);
        MailAccount account = Assert.Single(settings.MailAccounts);
        Assert.Equal(MailProviderType.Gmail, account.Provider);
        Assert.Equal("profile-user@gmail.test", account.EmailAddress);
        Assert.Equal(MailAuthenticationKind.OAuth, account.AuthenticationKind);
        MailCredential stored = Assert.Single(credentials.Values).Value;
        Assert.Equal(MailCredentialKind.GmailOAuthRefreshToken, stored.Kind);
        Assert.Equal("refresh-token-one", stored.Secret);
        Assert.DoesNotContain("access-token-one", stored.Secret, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmailConnection_RefreshTokenAndAuthorizationCodeNeverEnterSettings()
    {
        using TempFolder temp = new();
        AppSettings settings = new();
        RecordingCredentialStore credentials = new();
        QueueGmailOAuthService oauth = new();
        oauth.Enqueue("settings-user@gmail.test", "refresh-token-secret", "access-token-secret");
        MailAccountProvisioningService service = CreateProvisioner(settings, credentials, oauth);

        MailAccountProvisioningResult result = await service.ConnectGmailAsync();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        await new JsonSettingsService(settingsPath).SaveAsync(settings);
        string json = await File.ReadAllTextAsync(settingsPath);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("refresh-token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization-code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-test", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GmailOAuthProductionPathHasNoLoggerOrTokenDataStoreDependency()
    {
        Type[] dependencies = typeof(GmailOAuthService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(dependencies, type => type.Name.Contains("Logger", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, type => type.Name.Contains("DataStore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GmailConnection_ProfileFailureRollsBackCredentialAndCreatesNoMetadata()
    {
        AppSettings settings = new();
        RecordingCredentialStore credentials = new();
        QueueGmailOAuthService oauth = new() { FailProfile = true };
        oauth.Enqueue("unused@gmail.test", "refresh-token", "access-token");
        MailAccountProvisioningService service = CreateProvisioner(settings, credentials, oauth);

        MailAccountProvisioningResult result = await service.ConnectGmailAsync();

        Assert.False(result.IsSuccess);
        Assert.Empty(settings.MailAccounts);
        Assert.Empty(credentials.Values);
        Assert.Equal(1, credentials.DeleteCount);
    }

    [Fact]
    public async Task GmailConnection_DuplicateEmailDoesNotReplaceExistingCredential()
    {
        MailAccount existing = CreateGmailAccount("same@gmail.test");
        AppSettings settings = new() { MailAccounts = [existing] };
        RecordingCredentialStore credentials = new();
        credentials.Values[existing.CredentialKey] = MailCredential.CreateGmailOAuth(
            "working-refresh",
            "client-id-test",
            "client-secret-test");
        QueueGmailOAuthService oauth = new();
        oauth.Enqueue("SAME@gmail.test", "new-refresh", "new-access");
        MailAccountProvisioningService service = CreateProvisioner(settings, credentials, oauth);

        MailAccountProvisioningResult result = await service.ConnectGmailAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.AlreadyExists, result.FailureKind);
        Assert.Single(settings.MailAccounts);
        Assert.Single(credentials.Values);
        Assert.Equal("working-refresh", credentials.Values[existing.CredentialKey].Secret);
    }

    [Fact]
    public async Task GmailConnection_DifferentAccountsUseDistinctCredentialKeys()
    {
        AppSettings settings = new();
        RecordingCredentialStore credentials = new();
        QueueGmailOAuthService oauth = new();
        oauth.Enqueue("first@gmail.test", "first-refresh", "first-access");
        oauth.Enqueue("second@gmail.test", "second-refresh", "second-access");
        MailAccountProvisioningService service = CreateProvisioner(settings, credentials, oauth);

        Assert.True((await service.ConnectGmailAsync()).IsSuccess);
        Assert.True((await service.ConnectGmailAsync()).IsSuccess);

        Assert.Equal(2, settings.MailAccounts.Count);
        Assert.Equal(2, settings.MailAccounts.Select(account => account.CredentialKey).Distinct().Count());
        Assert.Equal(2, credentials.Values.Count);
    }

    [Fact]
    public async Task GmailMetadata_RestartRoundTripDoesNotRequireNetworkOrCredentialMaterial()
    {
        using TempFolder temp = new();
        MailAccount expected = CreateGmailAccount("restart@gmail.test");
        string path = Path.Combine(temp.Path, "settings.json");
        JsonSettingsService persistence = new(path);
        await persistence.SaveAsync(new AppSettings { MailAccounts = [expected] });

        SettingsLoadResult result = await persistence.LoadAsync();

        MailAccount actual = Assert.Single(result.Settings.MailAccounts);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.EmailAddress, actual.EmailAddress);
        Assert.Equal(expected.CredentialKey, actual.CredentialKey);
        Assert.Equal(MailAuthenticationKind.OAuth, actual.AuthenticationKind);
    }

    [Fact]
    public void GmailSelectionCreatesNoCoreWebViewController()
    {
        ServiceInstance telegram = CreateWebService();
        MailAccount gmail = CreateGmailAccount("navigation@gmail.test");
        AppSettings settings = new() { Services = [telegram], MailAccounts = [gmail] };
        RecordingSessionManager sessions = new();
        using MainWindowViewModel viewModel = CreateMainViewModel(settings, sessions);

        viewModel.SelectMailAccount(gmail.Id);

        Assert.Same(gmail, viewModel.SelectedMailAccount);
        Assert.Null(viewModel.SelectedService);
        Assert.Equal(0, sessions.InitializeCount);
    }

    [Fact]
    public async Task GmailDeleteRemovesOnlyItsMetadataAndCredential()
    {
        MailAccount gmail = CreateGmailAccount("delete@gmail.test");
        MailAccount yandex = CreatePasswordAccount(MailProviderType.Yandex, "keep@yandex.test");
        ServiceInstance telegram = CreateWebService();
        AppSettings settings = new() { Services = [telegram], MailAccounts = [gmail, yandex] };
        RecordingCredentialStore credentials = new();
        credentials.Values[gmail.CredentialKey] = MailCredential.CreateGmailOAuth(
            "gmail-refresh",
            "client-id-test",
            "client-secret-test");
        credentials.Values[yandex.CredentialKey] = MailCredential.CreatePassword("yandex-password");
        MailAccountProvisioningService service = CreateProvisioner(
            settings,
            credentials,
            new QueueGmailOAuthService());

        await service.DeleteAsync(gmail.Id);

        Assert.Equal(yandex.Id, Assert.Single(settings.MailAccounts).Id);
        Assert.DoesNotContain(gmail.CredentialKey, credentials.Values.Keys);
        Assert.Contains(yandex.CredentialKey, credentials.Values.Keys);
        Assert.Equal(telegram.Id, Assert.Single(settings.Services).Id);
        Assert.Equal("service-stable-profile", telegram.ProfileName);
    }

    [Fact]
    public async Task TypedCredentialStoreReadsLegacyStage7PasswordPayload()
    {
        using TempFolder temp = new();
        string key = Guid.NewGuid().ToString("N");
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy-app-password");
        byte[] protectedBytes = plaintext.Reverse().ToArray();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, key + ".credential.bin"), protectedBytes);
        FileMailCredentialStore store = new(temp.Path, new ReversibleProtector());

        MailCredential? credential = await store.LoadAsync(key);

        Assert.NotNull(credential);
        Assert.Equal(MailCredentialKind.Password, credential.Kind);
        Assert.Equal("legacy-app-password", credential.Secret);
    }

    [Fact]
    public async Task TypedCredentialStoreRoundTripsGmailOAuthPayload()
    {
        using TempFolder temp = new();
        string key = Guid.NewGuid().ToString("N");
        FileMailCredentialStore store = new(temp.Path, new ReversibleProtector());
        MailCredential expected = MailCredential.CreateGmailOAuth(
            "refresh-token-value",
            "client-id-value",
            "client-secret-value");

        await store.SaveAsync(key, expected);
        MailCredential? actual = await store.LoadAsync(key);

        Assert.Equal(expected, actual);
        Assert.Single(Directory.GetFiles(temp.Path, "*.credential.bin"));
    }

    [Fact]
    public async Task YandexPersistenceAndWebIdentityRemainUnchanged()
    {
        using TempFolder temp = new();
        ServiceInstance telegram = CreateWebService();
        MailAccount yandex = CreatePasswordAccount(MailProviderType.Yandex, "persist@yandex.test");
        string path = Path.Combine(temp.Path, "settings.json");
        JsonSettingsService persistence = new(path);
        await persistence.SaveAsync(new AppSettings { Services = [telegram], MailAccounts = [yandex] });

        SettingsLoadResult result = await persistence.LoadAsync();

        Assert.Equal(yandex.Id, Assert.Single(result.Settings.MailAccounts).Id);
        ServiceInstance restored = Assert.Single(result.Settings.Services);
        Assert.Equal(telegram.Id, restored.Id);
        Assert.Equal("service-stable-profile", restored.ProfileName);
    }

    private static MailAccountProvisioningService CreateProvisioner(
        AppSettings settings,
        RecordingCredentialStore credentials,
        IGmailOAuthService oauth) =>
        new(
            new MailProviderFactory([new GmailApiProvider()]),
            credentials,
            oauth,
            new RecordingSettingsStore(settings),
            TimeProvider.System);

    private static MainWindowViewModel CreateMainViewModel(
        AppSettings settings,
        RecordingSessionManager sessions)
    {
        MainWindowViewModel viewModel = new(
            new BuiltInServiceCatalog(),
            sessions,
            new RecordingSettingsStore(settings),
            new NoOpActivityCoordinator(),
            new NoOpNotificationCoordinator());
        viewModel.Initialize(settings);
        return viewModel;
    }

    private static MailAccount CreateGmailAccount(string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            EmailAddress = email,
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.OAuth,
            IsEnabled = true
        };

    private static MailAccount CreatePasswordAccount(MailProviderType provider, string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EmailAddress = email,
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.Password,
            IsEnabled = true
        };

    private static ServiceInstance CreateWebService() =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceType = ServiceType.Telegram,
            DisplayName = "Telegram",
            ProfileName = "service-stable-profile",
            IsEnabled = true
        };

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
            values[name] = value;
        }

        return values;
    }

    private sealed class OAuthFixture
    {
        public OAuthFixture(OAuthLoopbackResponse? callback = null, TimeSpan? timeout = null)
        {
            Browser = new RecordingBrowserLauncher();
            Protocol = new RecordingProtocolClient();
            Listener = new FakeLoopbackListener(
                new Uri("http://127.0.0.1:54321/oauth2/callback/"),
                _ => Task.FromResult(callback ?? new OAuthLoopbackResponse("authorization-code", "expected-state", null)));
            Service = CreateService(Listener, timeout ?? TimeSpan.FromSeconds(5));
        }

        private OAuthFixture(FakeLoopbackListener listener, TimeSpan timeout)
        {
            Browser = new RecordingBrowserLauncher();
            Protocol = new RecordingProtocolClient();
            Listener = listener;
            Service = CreateService(listener, timeout);
        }

        public GmailOAuthService Service { get; }
        public RecordingBrowserLauncher Browser { get; }
        public RecordingProtocolClient Protocol { get; }
        public FakeLoopbackListener Listener { get; }

        public static OAuthFixture WithPendingCallback(TimeSpan timeout)
        {
            FakeLoopbackListener listener = new(
                new Uri("http://127.0.0.1:54322/oauth2/callback/"),
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException();
                });
            return new OAuthFixture(listener, timeout);
        }

        private GmailOAuthService CreateService(FakeLoopbackListener listener, TimeSpan timeout) =>
            new(
                new StaticConfigurationSource(),
                Browser,
                new StaticStateGenerator("expected-state"),
                new FakeLoopbackListenerFactory(listener),
                Protocol,
                new GmailOAuthOptions(timeout));
    }

    private sealed class StaticConfigurationSource : IGoogleOAuthClientConfigurationSource
    {
        public string ConfigurationPath => "local-google-oauth-config";
        public Task<GoogleOAuthClientConfiguration?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GoogleOAuthClientConfiguration?>(
                new GoogleOAuthClientConfiguration("client-id-test", "client-secret-test"));
    }

    private sealed class RecordingBrowserLauncher : ISystemBrowserLauncher
    {
        public int OpenCount { get; private set; }
        public Uri? LastUri { get; private set; }
        public bool ShouldOpen { get; set; } = true;

        public bool TryOpen(Uri uri)
        {
            OpenCount++;
            LastUri = uri;
            return ShouldOpen;
        }
    }

    private sealed class StaticStateGenerator(string state) : IOAuthStateGenerator
    {
        public string CreateState() => state;
    }

    private sealed class FakeLoopbackListenerFactory(FakeLoopbackListener listener)
        : IOAuthLoopbackListenerFactory
    {
        public IOAuthLoopbackListener Create() => listener;
    }

    private sealed class FakeLoopbackListener(
        Uri redirectUri,
        Func<CancellationToken, Task<OAuthLoopbackResponse>> callback) : IOAuthLoopbackListener
    {
        public Uri RedirectUri { get; } = redirectUri;
        public bool IsDisposed { get; private set; }

        public Task<OAuthLoopbackResponse> WaitForCallbackAsync(
            CancellationToken cancellationToken = default) =>
            callback(cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProtocolClient : IGoogleOAuthProtocolClient
    {
        public int ExchangeCount { get; private set; }
        public Exception? ExchangeException { get; set; }
        public GoogleOAuthTokenResult TokenResult { get; set; } =
            new("access-token", "refresh-token");

        public GoogleOAuthAuthorizationRequest CreateAuthorizationRequest(
            GoogleOAuthClientConfiguration configuration,
            Uri redirectUri,
            string state) =>
            new(
                new Uri($"https://accounts.google.com/o/oauth2/v2/auth?state={Uri.EscapeDataString(state)}"),
                "pkce-code-verifier");

        public Task<GoogleOAuthTokenResult> ExchangeCodeAsync(
            GoogleOAuthClientConfiguration configuration,
            string authorizationCode,
            string codeVerifier,
            Uri redirectUri,
            CancellationToken cancellationToken = default)
        {
            ExchangeCount++;
            if (ExchangeException is not null)
            {
                return Task.FromException<GoogleOAuthTokenResult>(ExchangeException);
            }

            return Task.FromResult(TokenResult);
        }

        public Task<GmailUserProfile> GetProfileAsync(
            string accessToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailUserProfile("profile@gmail.test", "history"));
    }

    private sealed class QueueGmailOAuthService : IGmailOAuthService
    {
        private readonly Queue<(string Email, GmailOAuthSession Session)> _sessions = [];
        private (string Email, GmailOAuthSession Session)? _current;
        public bool FailProfile { get; set; }

        public void Enqueue(string email, string refreshToken, string accessToken) =>
            _sessions.Enqueue(
                (
                    email,
                    new GmailOAuthSession(
                        accessToken,
                        MailCredential.CreateGmailOAuth(
                            refreshToken,
                            "client-id-test",
                            "client-secret-test"))));

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            CancellationToken cancellationToken = default)
        {
            _current = _sessions.Dequeue();
            return Task.FromResult(GmailOAuthAuthorizationResult.Success(_current.Value.Session));
        }

        public Task<GmailProfileResult> GetProfileAsync(
            GmailOAuthSession session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                FailProfile
                    ? GmailProfileResult.Failure("Profile unavailable.")
                    : GmailProfileResult.Success(
                        new GmailUserProfile(_current!.Value.Email, "history-id")));
    }

    private sealed class RecordingCredentialStore : IMailCredentialStore
    {
        public Dictionary<string, MailCredential> Values { get; } = [];
        public int DeleteCount { get; private set; }

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
            Task.FromResult(Values.GetValueOrDefault(credentialKey));

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            Values.Remove(credentialKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettingsStore(AppSettings settings) : IApplicationSettingsStore
    {
        public AppSettings Current { get; private set; } = settings;
        public bool IsInitialized => true;
        public void Initialize(AppSettings value) => Current = value;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReversibleProtector : IMailCredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Reverse().ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray().Reverse().ToArray();
    }

    private sealed class RecordingSessionManager : IWebViewSessionManager
    {
        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested { add { } remove { } }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged { add { } remove { } }
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived { add { } remove { } }
        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted => false;
        public int InitializedSessionCount => 0;
        public int InitialNavigationCount => 0;
        public int InitializeCount { get; private set; }
        public Task<bool> InitializeAsync(IntPtr parentWindow, System.Drawing.Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return Task.FromResult(true);
        }
        public Task<bool> PrimeAsync(IntPtr parentWindow, System.Drawing.Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool IsSessionInitialized(Guid serviceInstanceId) => false;
        public void ActivateSession(Guid serviceInstanceId, System.Drawing.Rectangle bounds, bool isVisible, bool moveFocus = false) { }
        public void UpdateActiveSessionLayout(System.Drawing.Rectangle bounds, bool isVisible) { }
        public void NotifyParentWindowPositionChanged() { }
        public bool HasSession(Guid serviceInstanceId) => false;
        public void DeactivateSession() { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId) { }
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

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "UnifiedMessenger.Tests",
                Guid.NewGuid().ToString("N"));
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
}
