using System.Text.Json;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailReauthenticationTests
{
    [Fact]
    public async Task AuthRequired_UsesTypedGoogleReauthenticationInsteadOfRetry()
    {
        QueueReadProvider provider = new();
        provider.EnqueueFailure(AuthRequired());
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = ViewModel(provider, reauthentication);

        await viewModel.ActivateAsync(GmailAccount("account@gmail.test"));

        Assert.Equal(MailReadFailureKind.ReauthorizationRequired, viewModel.FailureKind);
        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.Equal("Google sign-in required", viewModel.ErrorTitle);
        Assert.Equal(
            "The connection expired or access was revoked. Sign in to Google again to continue receiving mail.",
            viewModel.ListErrorDescription);
        Assert.True(viewModel.ReauthenticateGmailCommand.CanExecute(null));
        Assert.False(viewModel.RetryCommand.CanExecute(null));
        Assert.False(viewModel.ShowTransientRetryAction);
    }

    [Fact]
    public async Task TransientFailure_KeepsRetryAsPrimaryRecovery()
    {
        QueueReadProvider provider = new();
        provider.EnqueueFailure(new MailReadException(
            MailReadFailureKind.ConnectionFailed,
            "Не удалось загрузить почту. Проверьте подключение к сети."));
        using MailInboxViewModel viewModel = ViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()));

        await viewModel.ActivateAsync(GmailAccount("account@gmail.test"));

        Assert.False(viewModel.RequiresGmailReauthentication);
        Assert.True(viewModel.ShowTransientRetryAction);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        Assert.False(viewModel.ReauthenticateGmailCommand.CanExecute(null));
    }

    [Fact]
    public async Task SuccessfulReauthentication_ClearsErrorAndRefreshesSameAccount()
    {
        MailAccount account = GmailAccount("account@gmail.test");
        QueueReadProvider provider = new();
        provider.EnqueueFailure(AuthRequired());
        provider.EnqueuePage(Page("after-reauth"));
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = ViewModel(provider, reauthentication);
        await viewModel.ActivateAsync(account);

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Equal([account.Id], reauthentication.AccountIds);
        Assert.False(viewModel.HasListError);
        Assert.Null(viewModel.FailureKind);
        Assert.Equal("after-reauth", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal(2, provider.PageCallCount);
        Assert.Same(account, viewModel.ActiveAccount);
    }

    [Fact]
    public async Task CanceledReauthentication_LeavesTypedAuthStateAndDoesNotRefresh()
    {
        QueueReadProvider provider = new();
        provider.EnqueueFailure(AuthRequired());
        using MailInboxViewModel viewModel = ViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Canceled()));
        await viewModel.ActivateAsync(GmailAccount("account@gmail.test"));

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.False(viewModel.HasGmailReauthenticationError);
        Assert.Equal(1, provider.PageCallCount);
    }

    [Fact]
    public async Task FailedReauthentication_HasNoFalseSuccessAndCanBeInvokedAgain()
    {
        QueueReadProvider provider = new();
        provider.EnqueueFailure(AuthRequired());
        using MailInboxViewModel viewModel = ViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Failure()));
        await viewModel.ActivateAsync(GmailAccount("account@gmail.test"));

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.Equal("Could not sign in to Google. Please retry.", viewModel.GmailReauthenticationErrorMessage);
        Assert.True(viewModel.ReauthenticateGmailCommand.CanExecute(null));
        Assert.Equal(1, provider.PageCallCount);
    }

    [Fact]
    public async Task SameGoogleIdentity_ReplacesCredentialUnderExistingKeyAndPreservesAccountSettings()
    {
        MailAccount account = GmailAccount("same@gmail.test");
        account.DisplayName = "Рабочая почта";
        account.SortOrder = 4;
        account.IsEnabled = true;
        account.LastSuccessfulConnectionUtc = DateTimeOffset.Parse("2026-08-01T10:15:00Z");
        AppSettings settings = new()
        {
            MailAccounts = [account],
            Notifications = new NotificationSettings
            {
                IsEnabled = false,
                PlaySound = false,
                DoNotDisturb = true
            }
        };
        string settingsBefore = JsonSerializer.Serialize(settings);
        MailCredential oldCredential = Credential("old-refresh", GmailOAuthConstants.ModifyScope);
        MailCredential newCredential = Credential("new-refresh", GmailOAuthConstants.ModifyScope);
        RecordingCredentialStore store = new((account.CredentialKey, oldCredential));
        RecordingOAuthService oauth = RecordingOAuthService.Success("same@gmail.test", newCredential);
        GmailReauthenticationService service = new(store, oauth);

        GmailReauthenticationResult result = await service.ReauthenticateAsync(account);

        Assert.True(result.IsSuccess);
        Assert.Equal(GmailOAuthConstants.ModifyScope, oauth.RequestedScope);
        Assert.Equal(account.CredentialKey, Assert.Single(store.SavedKeys));
        Assert.Same(newCredential, store.Values[account.CredentialKey]);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData(MailConnectionFailureKind.OperationCanceled, GmailReauthenticationOutcome.Canceled)]
    [InlineData(MailConnectionFailureKind.OAuthDenied, GmailReauthenticationOutcome.Canceled)]
    [InlineData(MailConnectionFailureKind.OAuthTokenExchangeFailed, GmailReauthenticationOutcome.Failed)]
    public async Task OAuthCancelOrFailure_PreservesExistingCredential(
        MailConnectionFailureKind failureKind,
        GmailReauthenticationOutcome expectedOutcome)
    {
        MailAccount account = GmailAccount("same@gmail.test");
        MailCredential oldCredential = Credential("old-refresh", GmailOAuthConstants.ModifyScope);
        RecordingCredentialStore store = new((account.CredentialKey, oldCredential));
        RecordingOAuthService oauth = RecordingOAuthService.Failure(failureKind);

        GmailReauthenticationResult result = await new GmailReauthenticationService(store, oauth)
            .ReauthenticateAsync(account);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Same(oldCredential, store.Values[account.CredentialKey]);
        Assert.Empty(store.SavedKeys);
    }

    [Fact]
    public async Task WrongGoogleIdentity_DoesNotReplaceExistingAccountCredential()
    {
        MailAccount account = GmailAccount("expected@gmail.test");
        MailCredential oldCredential = Credential("old-refresh", GmailOAuthConstants.ModifyScope);
        RecordingCredentialStore store = new((account.CredentialKey, oldCredential));
        RecordingOAuthService oauth = RecordingOAuthService.Success(
            "different@gmail.test",
            Credential("other-refresh", GmailOAuthConstants.ModifyScope));

        GmailReauthenticationResult result = await new GmailReauthenticationService(store, oauth)
            .ReauthenticateAsync(account);

        Assert.Equal(GmailReauthenticationOutcome.WrongAccount, result.Outcome);
        Assert.Equal(
            "You signed in to another Google account. Sign in as expected@gmail.test.",
            result.UserMessage);
        Assert.Same(oldCredential, store.Values[account.CredentialKey]);
        Assert.Empty(store.SavedKeys);
    }

    [Fact]
    public async Task ReauthenticationOfGmailA_LeavesGmailBAndAccountCollectionUntouched()
    {
        MailAccount first = GmailAccount("first@gmail.test");
        MailAccount second = GmailAccount("second@gmail.test");
        AppSettings settings = new() { MailAccounts = [first, second] };
        MailCredential firstOld = Credential("first-old", GmailOAuthConstants.ModifyScope);
        MailCredential secondCredential = Credential("second", GmailOAuthConstants.ModifyScope);
        RecordingCredentialStore store = new(
            (first.CredentialKey, firstOld),
            (second.CredentialKey, secondCredential));
        RecordingOAuthService oauth = RecordingOAuthService.Success(
            first.EmailAddress,
            Credential("first-new", GmailOAuthConstants.ModifyScope));

        GmailReauthenticationResult result = await new GmailReauthenticationService(store, oauth)
            .ReauthenticateAsync(first);

        Assert.True(result.IsSuccess);
        Assert.Equal([first, second], settings.MailAccounts);
        Assert.Same(secondCredential, store.Values[second.CredentialKey]);
        Assert.DoesNotContain(second.CredentialKey, store.SavedKeys);
    }

    [Fact]
    public async Task ConcurrentReauthenticationForSameAccount_UsesSingleOAuthFlow()
    {
        MailAccount account = GmailAccount("same@gmail.test");
        RecordingCredentialStore store = new((
            account.CredentialKey,
            Credential("old-refresh", GmailOAuthConstants.ModifyScope)));
        ControlledOAuthService oauth = new((
            account.EmailAddress,
            Credential("new-refresh", GmailOAuthConstants.ModifyScope)));
        GmailReauthenticationService service = new(store, oauth);

        Task<GmailReauthenticationResult> first = service.ReauthenticateAsync(account);
        Task<GmailReauthenticationResult> second = service.ReauthenticateAsync(account);

        Assert.Equal(1, oauth.AuthorizationCount);
        oauth.CompleteAuthorization(0);
        GmailReauthenticationResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, oauth.AuthorizationCount);
        Assert.Equal([account.CredentialKey], store.SavedKeys);
    }

    [Fact]
    public async Task ConcurrentReauthenticationForDifferentAccounts_UsesIndependentOAuthFlows()
    {
        MailAccount firstAccount = GmailAccount("first@gmail.test");
        MailAccount secondAccount = GmailAccount("second@gmail.test");
        RecordingCredentialStore store = new(
            (
                firstAccount.CredentialKey,
                Credential("first-old", GmailOAuthConstants.ModifyScope)),
            (
                secondAccount.CredentialKey,
                Credential("second-old", GmailOAuthConstants.ModifyScope)));
        ControlledOAuthService oauth = new(
            (
                firstAccount.EmailAddress,
                Credential("first-new", GmailOAuthConstants.ModifyScope)),
            (
                secondAccount.EmailAddress,
                Credential("second-new", GmailOAuthConstants.ModifyScope)));
        GmailReauthenticationService service = new(store, oauth);

        Task<GmailReauthenticationResult> first = service.ReauthenticateAsync(firstAccount);
        Task<GmailReauthenticationResult> second = service.ReauthenticateAsync(secondAccount);

        Assert.Equal(2, oauth.AuthorizationCount);
        oauth.CompleteAuthorization(1);
        Assert.True((await second).IsSuccess);
        Assert.False(first.IsCompleted);

        oauth.CompleteAuthorization(0);
        Assert.True((await first).IsSuccess);
        Assert.Contains(firstAccount.CredentialKey, store.SavedKeys);
        Assert.Contains(secondAccount.CredentialKey, store.SavedKeys);
    }

    [Fact]
    public async Task NonGmailService_IsRejectedWithoutOAuthOrCredentialChanges()
    {
        MailAccount account = GmailAccount("imap@example.test");
        account.Provider = MailProviderType.GenericImap;
        RecordingCredentialStore store = new();
        RecordingOAuthService oauth = RecordingOAuthService.Success(
            account.EmailAddress,
            Credential("unused", GmailOAuthConstants.ModifyScope));

        GmailReauthenticationResult result = await new GmailReauthenticationService(store, oauth)
            .ReauthenticateAsync(account);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, oauth.AuthorizationCount);
        Assert.Empty(store.SavedKeys);
    }

    [Fact]
    public void MailUi_ContainsDistinctGoogleLoginAndRetryActions()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "UnifiedMessenger.App",
            "Views",
            "MailInboxView.xaml"));

        Assert.Contains("Content=\"{loc:Text Key='Sign in to Google'}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ReauthenticateGmailCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{loc:Text Key='Retry'}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowTransientRetryAction", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding RequiresGmailMessageReauthentication", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding RequiresGmailReadStateReauthentication", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding RequiresGmailComposeReauthentication", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowMessageRetryAction", xaml, StringComparison.Ordinal);
    }

    private static MailInboxViewModel ViewModel(
        QueueReadProvider provider,
        IGmailReauthenticationService reauthentication) =>
        new(new ReadProviderFactory(provider, reauthentication));

    private static MailAccount GmailAccount(string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            EmailAddress = email,
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.OAuth,
            IsEnabled = true,
            SortOrder = 2
        };

    private static MailReadException AuthRequired() =>
        new(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");

    private static MailCredential Credential(string refreshToken, string scope) =>
        MailCredential.CreateGmailOAuth(refreshToken, "client", "secret", scope);

    private static MailPage<MailMessageSummary> Page(string key) =>
        new(
            [new MailMessageSummary(
                key,
                "Subject",
                "Sender",
                "sender@example.test",
                DateTimeOffset.UtcNow,
                "Preview",
                true)],
            null);

    private static string FindRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Repository file was not found.", Path.Combine(parts));
    }

    private sealed class ReadProviderFactory(
        IMailReadProvider provider,
        IGmailReauthenticationService reauthentication) : IMailReadProviderFactory
    {
        public IGmailReauthenticationService? GmailReauthenticationService => reauthentication;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class QueueReadProvider : IMailReadProvider
    {
        private readonly Queue<object> _pages = new();

        public int PageCallCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;
        public void EnqueueFailure(MailReadException exception) => _pages.Enqueue(exception);
        public void EnqueuePage(MailPage<MailMessageSummary> page) => _pages.Enqueue(page);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageCallCount++;
            object next = _pages.Dequeue();
            return next is Exception exception
                ? Task.FromException<MailPage<MailMessageSummary>>(exception)
                : Task.FromResult((MailPage<MailMessageSummary>)next);
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailMessageContent>(new NotSupportedException());
    }

    private sealed class RecordingReauthenticationService(GmailReauthenticationResult result)
        : IGmailReauthenticationService
    {
        public List<Guid> AccountIds { get; } = [];

        public Task<GmailReauthenticationResult> ReauthenticateAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            AccountIds.Add(account.Id);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingCredentialStore(params (string Key, MailCredential Value)[] credentials)
        : IMailCredentialStore
    {
        public Dictionary<string, MailCredential> Values { get; } =
            credentials.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        public List<string> SavedKeys { get; } = [];

        public Task SaveAsync(
            string credentialKey,
            MailCredential credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedKeys.Add(credentialKey);
            Values[credentialKey] = credential;
            return Task.CompletedTask;
        }

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.TryGetValue(credentialKey, out MailCredential? credential);
            return Task.FromResult(credential);
        }

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOAuthService : IGmailOAuthService
    {
        private readonly GmailOAuthAuthorizationResult _authorization;
        private readonly GmailProfileResult _profile;

        private RecordingOAuthService(
            GmailOAuthAuthorizationResult authorization,
            GmailProfileResult profile)
        {
            _authorization = authorization;
            _profile = profile;
        }

        public int AuthorizationCount { get; private set; }
        public string? RequestedScope { get; private set; }

        public static RecordingOAuthService Success(string email, MailCredential credential)
        {
            GmailOAuthSession session = new("access", credential);
            return new RecordingOAuthService(
                GmailOAuthAuthorizationResult.Success(session),
                GmailProfileResult.Success(new GmailUserProfile(email, null)));
        }

        public static RecordingOAuthService Failure(MailConnectionFailureKind failureKind) =>
            new(
                GmailOAuthAuthorizationResult.Failure(failureKind, "sanitized failure"),
                GmailProfileResult.Failure("unused"));

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            CancellationToken cancellationToken = default) =>
            AuthorizeAsync(GmailOAuthConstants.ReadOnlyScope, cancellationToken);

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            string scope,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            RequestedScope = scope;
            return Task.FromResult(_authorization);
        }

        public Task<GmailProfileResult> GetProfileAsync(
            GmailOAuthSession session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class ControlledOAuthService : IGmailOAuthService
    {
        private readonly (GmailOAuthSession Session, GmailProfileResult Profile)[] _calls;
        private readonly TaskCompletionSource<GmailOAuthAuthorizationResult>[] _authorizations;
        private int _authorizationCount;

        public ControlledOAuthService(params (string Email, MailCredential Credential)[] calls)
        {
            _calls = calls
                .Select((call, index) =>
                {
                    GmailOAuthSession session = new($"access-{index}", call.Credential);
                    return (
                        session,
                        GmailProfileResult.Success(new GmailUserProfile(call.Email, null)));
                })
                .ToArray();
            _authorizations = calls
                .Select(_ => new TaskCompletionSource<GmailOAuthAuthorizationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        }

        public int AuthorizationCount => Volatile.Read(ref _authorizationCount);

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            CancellationToken cancellationToken = default) =>
            AuthorizeAsync(GmailOAuthConstants.ReadOnlyScope, cancellationToken);

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            string scope,
            CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref _authorizationCount) - 1;
            return _authorizations[index].Task.WaitAsync(cancellationToken);
        }

        public Task<GmailProfileResult> GetProfileAsync(
            GmailOAuthSession session,
            CancellationToken cancellationToken = default)
        {
            int index = Array.FindIndex(
                _calls,
                call => ReferenceEquals(call.Session, session));
            return Task.FromResult(_calls[index].Profile);
        }

        public void CompleteAuthorization(int index) =>
            _authorizations[index].TrySetResult(
                GmailOAuthAuthorizationResult.Success(_calls[index].Session));
    }
}
