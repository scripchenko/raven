using System.Xml.Linq;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class YandexPasswordReplacementTests
{
    [Fact]
    public async Task SuccessfulReplacementValidatesBeforeReplacingSameCredentialKey()
    {
        List<string> events = [];
        RecordingProvider provider = new(events);
        MailAccount account = Account("one@yandex.test");
        RecordingCredentialStore credentials = new(events);
        credentials.Values[account.CredentialKey] = MailCredential.CreatePassword("old-password");
        RecordingSettingsStore settings = new(new AppSettings { MailAccounts = [account] });
        MailAccountProvisioningService service = Service(provider, credentials, settings);
        Guid originalId = account.Id;

        MailAccountPasswordReplacementResult result = await service.ReplaceYandexPasswordAsync(
            account,
            "new-password");

        Assert.True(result.IsSuccess);
        Assert.Equal(["validate", "save"], events);
        Assert.Equal("new-password", credentials.Values[account.CredentialKey].Secret);
        Assert.Equal(originalId, account.Id);
        Assert.Same(account, Assert.Single(settings.Current.MailAccounts));
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task FailedValidationKeepsOldCredentialAndRetryCanSucceed()
    {
        RecordingProvider provider = new([])
        {
            Result = MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.AuthenticationFailed,
                "IMAP отклонил учётные данные.")
        };
        MailAccount account = Account("one@yandex.test");
        RecordingCredentialStore credentials = new([]);
        credentials.Values[account.CredentialKey] = MailCredential.CreatePassword("old-password");
        MailAccountProvisioningService service = Service(
            provider,
            credentials,
            new RecordingSettingsStore(new AppSettings { MailAccounts = [account] }));

        MailAccountPasswordReplacementResult failed = await service.ReplaceYandexPasswordAsync(
            account,
            "wrong-password");

        Assert.False(failed.IsSuccess);
        Assert.Equal(MailConnectionFailureKind.AuthenticationFailed, failed.FailureKind);
        Assert.Equal("old-password", credentials.Values[account.CredentialKey].Secret);
        Assert.Equal(0, credentials.SaveCount);

        provider.Result = MailConnectionValidationResult.Success(new MailIdentity(account.EmailAddress, null));
        MailAccountPasswordReplacementResult retried = await service.ReplaceYandexPasswordAsync(
            account,
            "correct-password");

        Assert.True(retried.IsSuccess);
        Assert.Equal("correct-password", credentials.Values[account.CredentialKey].Secret);
        Assert.Equal(1, credentials.SaveCount);
    }

    [Fact]
    public async Task ReplacementIsAccountScoped()
    {
        RecordingProvider provider = new([]);
        MailAccount first = Account("first@yandex.test");
        MailAccount second = Account("second@yandex.test");
        RecordingCredentialStore credentials = new([]);
        credentials.Values[first.CredentialKey] = MailCredential.CreatePassword("first-old");
        credentials.Values[second.CredentialKey] = MailCredential.CreatePassword("second-old");
        MailAccountProvisioningService service = Service(
            provider,
            credentials,
            new RecordingSettingsStore(new AppSettings { MailAccounts = [first, second] }));

        await service.ReplaceYandexPasswordAsync(first, "first-new");

        Assert.Equal("first-new", credentials.Values[first.CredentialKey].Secret);
        Assert.Equal("second-old", credentials.Values[second.CredentialKey].Secret);
        Assert.Equal(first.EmailAddress, Assert.Single(provider.Requests).EmailAddress);
    }

    [Fact]
    public async Task GmailAccountCannotEnterPasswordReplacementFlow()
    {
        RecordingProvider provider = new([]);
        MailAccount gmail = WithProvider(
            Account("user@gmail.test"),
            MailProviderType.Gmail,
            MailAuthenticationKind.OAuth);
        RecordingCredentialStore credentials = new([]);
        credentials.Values[gmail.CredentialKey] = MailCredential.CreateGmailOAuth("refresh", "client", "secret");
        MailAccountProvisioningService service = Service(
            provider,
            credentials,
            new RecordingSettingsStore(new AppSettings { MailAccounts = [gmail] }));

        MailAccountPasswordReplacementResult result = await service.ReplaceYandexPasswordAsync(
            gmail,
            "not-a-google-password");

        Assert.False(result.IsSuccess);
        Assert.Empty(provider.Requests);
        Assert.Equal(0, credentials.SaveCount);
        Assert.Equal(MailCredentialKind.GmailOAuthRefreshToken, credentials.Values[gmail.CredentialKey].Kind);
    }

    [Fact]
    public void PasswordUiStartsEmptyAndSettingsExposeActionOnlyThroughYandexFlag()
    {
        XDocument dialog = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "ChangeMailAppPasswordWindow.xaml"));
        XDocument settings = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "SettingsView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement passwordBox = Assert.Single(dialog.Descendants(presentation + "PasswordBox"));
        XElement action = Assert.Single(settings.Descendants(presentation + "Button"), element =>
            (string?)element.Attribute("Content") == "Изменить пароль приложения");

        Assert.Null(passwordBox.Attribute("Password"));
        Assert.DoesNotContain("Binding", passwordBox.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "CanChangeAppPassword",
            (string?)action.Attribute("Visibility") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(MailAccount).GetProperties(), property =>
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActiveYandexAccountRebaselinesAfterPasswordReplacementWithoutChangingIdentity()
    {
        MailAccount account = Account("one@yandex.test");
        RecordingReadProvider provider = new();
        using MailInboxViewModel viewModel = new(new ReadProviderFactory(provider));
        await viewModel.ActivateAsync(account);
        Guid originalId = account.Id;

        await viewModel.RefreshAfterPasswordReplacementAsync(account);

        Assert.Equal(originalId, viewModel.ActiveAccount?.Id);
        Assert.Equal(2, provider.FolderLoadCount);
        Assert.Equal(2, provider.PageLoadCount);
        Assert.True(viewModel.HasLoaded);
        Assert.False(viewModel.HasListError);
    }

    private static MailAccountProvisioningService Service(
        IMailProvider provider,
        IMailCredentialStore credentials,
        IApplicationSettingsStore settings) =>
        new(
            new MailProviderFactory([provider]),
            credentials,
            new NoOpGmailOAuthService(),
            settings,
            TimeProvider.System);

    private static MailAccount Account(string email) => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Yandex,
        EmailAddress = email,
        DisplayName = "Работа",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailAccount WithProvider(
        MailAccount account,
        MailProviderType provider,
        MailAuthenticationKind authenticationKind)
    {
        account.Provider = provider;
        account.AuthenticationKind = authenticationKind;
        return account;
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(Path.Combine(relativePath));
    }

    private sealed class RecordingProvider(List<string> events) : IMailProvider
    {
        public MailProviderType ProviderType => MailProviderType.Yandex;
        public MailProviderDescriptor Descriptor { get; } = new(
            MailProviderType.Yandex,
            "Яндекс Почта",
            MailAuthenticationKind.Password,
            MailProviderCapabilities.ConnectionValidation,
            string.Empty);
        public MailConnectionValidationResult Result { get; set; } =
            MailConnectionValidationResult.Success(new MailIdentity("one@yandex.test", null));
        public List<MailAccountConnectionRequest> Requests { get; } = [];

        public Task<MailConnectionValidationResult> ValidateAsync(
            MailAccountConnectionRequest request,
            string secret,
            CancellationToken cancellationToken = default)
        {
            events.Add("validate");
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCredentialStore(List<string> events) : IMailCredentialStore
    {
        public Dictionary<string, MailCredential> Values { get; } = [];
        public int SaveCount { get; private set; }

        public Task SaveAsync(string credentialKey, MailCredential credential, CancellationToken cancellationToken = default)
        {
            events.Add("save");
            SaveCount++;
            Values[credentialKey] = credential;
            return Task.CompletedTask;
        }

        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(credentialKey));

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

    private sealed class NoOpGmailOAuthService : IGmailOAuthService
    {
        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GmailProfileResult> GetProfileAsync(GmailOAuthSession session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReadProvider : IMailReadProvider
    {
        public int FolderLoadCount { get; private set; }
        public int PageLoadCount { get; private set; }
        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Yandex;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            FolderLoadCount++;
            return Task.FromResult<IReadOnlyList<MailFolder>>
                ([MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX")]);
        }

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageLoadCount++;
            return Task.FromResult(new MailPage<MailMessageSummary>([], null, 0));
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetPageAsync(
                account,
                MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
                continuationToken,
                pageSize,
                cancellationToken);

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReadProviderFactory(RecordingReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }
}
