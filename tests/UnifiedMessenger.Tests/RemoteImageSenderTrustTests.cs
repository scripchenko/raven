using System.Text;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

public sealed class RemoteImageSenderTrustTests
{
    [Fact]
    public async Task UnknownSender_RemainsBlockedAndOffersOneTimeAndAlwaysActions()
    {
        MailAccount account = Account();
        MailMessageSummary summary = Summary("first");
        InMemoryTrustStore trustStore = new();
        using MailInboxViewModel viewModel = ViewModel(account, summary, Content("first", "Sender@Example.test"), trustStore);

        await OpenAsync(viewModel, account, summary);

        Assert.False(viewModel.IsCurrentRemoteImageSenderTrusted);
        Assert.True(viewModel.ShowRemoteImagesBanner);
        Assert.True(viewModel.ShowOneTimeRemoteImagesAction);
        Assert.True(viewModel.ShowAlwaysRemoteImagesFromSenderAction);
        Assert.False(viewModel.ShowRevokeRemoteImagesFromSenderAction);
    }

    [Fact]
    public async Task AlwaysTrust_AppliesToFutureMessagesFromSameSenderInSameAccount()
    {
        MailAccount account = Account();
        MailMessageSummary first = Summary("first");
        MailMessageSummary second = Summary("second");
        InMemoryTrustStore trustStore = new();
        QueueProvider provider = new(account, [first, second], key => Content(key, "Sender@Example.test"));
        using MailInboxViewModel viewModel = new(new ProviderFactory(provider), remoteImageSenderTrustStore: trustStore);
        await OpenAsync(viewModel, account, first);

        await viewModel.TrustCurrentRemoteImageSenderAsync();
        viewModel.MarkRemoteImagesShown();
        viewModel.SelectedMessageSummary = second;
        await viewModel.CurrentMessageLoadTask;
        await viewModel.CurrentRemoteImageSenderTrustTask;

        Assert.True(viewModel.IsCurrentRemoteImageSenderTrusted);
        Assert.False(viewModel.ShowOneTimeRemoteImagesAction);
        Assert.True(viewModel.ShowRevokeRemoteImagesFromSenderAction);
        Assert.True(MainWindow.ShouldLoadTrustedRemoteImages(
            viewModel,
            nameof(MailInboxViewModel.IsCurrentRemoteImageSenderTrusted)));
        Assert.True(await trustStore.IsTrustedAsync(account.Id, "sender@example.test"));
    }

    [Fact]
    public async Task Trust_IsIsolatedByAccountAndSender()
    {
        InMemoryTrustStore store = new();
        Guid firstAccount = Guid.NewGuid();
        Guid secondAccount = Guid.NewGuid();
        await store.TrustAsync(firstAccount, "trusted@example.test");

        Assert.True(await store.IsTrustedAsync(firstAccount, "trusted@example.test"));
        Assert.False(await store.IsTrustedAsync(firstAccount, "other@example.test"));
        Assert.False(await store.IsTrustedAsync(secondAccount, "trusted@example.test"));
    }

    [Fact]
    public async Task Revoke_RemovesFutureAutomaticTrustButKeepsCurrentSessionConsent()
    {
        MailAccount account = Account();
        MailMessageSummary summary = Summary("first");
        MailMessageSummary next = Summary("next");
        InMemoryTrustStore trustStore = new();
        await trustStore.TrustAsync(account.Id, "sender@example.test");
        QueueProvider provider = new(account, [summary, next], key => Content(key, "sender@example.test"));
        using MailInboxViewModel viewModel = new(new ProviderFactory(provider), remoteImageSenderTrustStore: trustStore);
        await OpenAsync(viewModel, account, summary);
        viewModel.MarkRemoteImagesShown();

        await viewModel.RevokeCurrentRemoteImageSenderTrustAsync();

        Assert.False(viewModel.IsCurrentRemoteImageSenderTrusted);
        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.False(viewModel.ShowRemoteImagesBanner);
        Assert.False(await trustStore.IsTrustedAsync(account.Id, "sender@example.test"));

        viewModel.SelectedMessageSummary = next;
        await viewModel.CurrentMessageLoadTask;
        await viewModel.CurrentRemoteImageSenderTrustTask;
        Assert.False(viewModel.IsCurrentRemoteImageSenderTrusted);
        Assert.True(viewModel.ShowOneTimeRemoteImagesAction);
    }

    [Fact]
    public async Task PersistentTrust_SurvivesStoreRecreationAndBlobContainsNoPlaintextSender()
    {
        string folder = TemporaryFolder();
        Guid accountId = Guid.NewGuid();
        const string sender = "sender@example.test";
        try
        {
            using (FileRemoteImageSenderTrustStore first = new(folder, new DpapiRemoteImageSenderTrustProtector()))
            {
                await first.TrustAsync(accountId, sender);
            }

            string file = Assert.Single(Directory.GetFiles(folder, "*.trust.bin"));
            byte[] protectedBlob = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain(sender, Encoding.Latin1.GetString(protectedBlob), StringComparison.OrdinalIgnoreCase);

            using FileRemoteImageSenderTrustStore restarted = new(folder, new DpapiRemoteImageSenderTrustProtector());
            Assert.True(await restarted.IsTrustedAsync(accountId, sender));
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    [Fact]
    public async Task AccountDeletion_RemovesOnlyThatAccountsEncryptedTrustBlob()
    {
        string folder = TemporaryFolder();
        Guid removedAccount = Guid.NewGuid();
        Guid retainedAccount = Guid.NewGuid();
        try
        {
            using FileRemoteImageSenderTrustStore store = new(folder, new DpapiRemoteImageSenderTrustProtector());
            await store.TrustAsync(removedAccount, "sender@example.test");
            await store.TrustAsync(retainedAccount, "sender@example.test");

            await store.DeleteAccountAsync(removedAccount);

            Assert.False(await store.IsTrustedAsync(removedAccount, "sender@example.test"));
            Assert.True(await store.IsTrustedAsync(retainedAccount, "sender@example.test"));
            Assert.Single(Directory.GetFiles(folder, "*.trust.bin"));
        }
        finally
        {
            DeleteTemporaryFolder(folder);
        }
    }

    [Fact]
    public async Task MissingInvalidOrAmbiguousFrom_DoesNotOfferPersistentTrust()
    {
        MailAccount account = Account();
        MailMessageSummary summary = Summary("first");
        InMemoryTrustStore trustStore = new();
        MailMessageContent ambiguous = Content("first", "sender@example.test") with
        {
            HasUnambiguousFromAddress = false
        };
        using MailInboxViewModel viewModel = ViewModel(account, summary, ambiguous, trustStore);

        await OpenAsync(viewModel, account, summary);

        Assert.False(viewModel.CanTrustCurrentRemoteImageSender);
        Assert.False(viewModel.ShowAlwaysRemoteImagesFromSenderAction);
        Assert.True(viewModel.ShowOneTimeRemoteImagesAction);
        Assert.False(RemoteImageSenderIdentity.TryNormalize("not an address", out _));
        Assert.False(RemoteImageSenderIdentity.TryNormalize("first@example.test, second@example.test", out _));
    }

    [Fact]
    public async Task Extractor_MarksMultipleFromMailboxesAsAmbiguous()
    {
        MimeKit.MimeMessage message = new()
        {
            Subject = "Subject",
            Body = new MimeKit.TextPart("html")
            {
                Text = "<p>Body</p><img src='https://images.example.test/photo.png'>"
            }
        };
        message.From.Add(new MimeKit.MailboxAddress("First", "first@example.test"));
        message.From.Add(new MimeKit.MailboxAddress("Second", "second@example.test"));
        message.To.Add(new MimeKit.MailboxAddress("Recipient", "recipient@example.test"));

        MailMessageContent content = new MailContentExtractor(new MailHtmlSanitizer()).Extract("key", message, false);

        Assert.False(content.HasUnambiguousFromAddress);
    }

    [Fact]
    public void Trust_IsNotAddedToSettingsSchema()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());

        Assert.DoesNotContain("RemoteImageSender", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => property.Name.Contains("Trust", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task OpenAsync(
        MailInboxViewModel viewModel,
        MailAccount account,
        MailMessageSummary summary)
    {
        await viewModel.ActivateAsync(account);
        viewModel.SelectedMessageSummary = summary;
        await viewModel.CurrentMessageLoadTask;
        await viewModel.CurrentRemoteImageSenderTrustTask;
    }

    private static MailInboxViewModel ViewModel(
        MailAccount account,
        MailMessageSummary summary,
        MailMessageContent content,
        IRemoteImageSenderTrustStore trustStore)
    {
        QueueProvider provider = new(account, [summary], _ => content);
        return new MailInboxViewModel(new ProviderFactory(provider), remoteImageSenderTrustStore: trustStore);
    }

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        EmailAddress = "account@example.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.OAuth,
        IsEnabled = true
    };

    private static MailMessageSummary Summary(string key) => new(
        key,
        "Subject",
        "Sender",
        "sender@example.test",
        DateTimeOffset.UtcNow,
        "Preview",
        true);

    private static MailMessageContent Content(string key, string sender) => new(
        key,
        "Subject",
        "Sender",
        sender,
        "recipient@example.test",
        DateTimeOffset.UtcNow,
        MailMessageBodyKind.SanitizedHtml,
        "<p>Body</p><img data-um-remote-image-id='remote-1'>",
        [new MailRemoteImageReference("remote-1", new Uri("https://images.example.test/photo.png"))],
        true,
        false)
    {
        HasUnambiguousFromAddress = true
    };

    private static string TemporaryFolder() =>
        Path.Combine(Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class ProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class QueueProvider(
        MailAccount account,
        IReadOnlyList<MailMessageSummary> summaries,
        Func<string, MailMessageContent> contentFactory) : IMailReadProvider
    {
        public bool Supports(MailProviderType providerType) => providerType == account.Provider;

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount requestedAccount,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>(summaries, null));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount requestedAccount,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(contentFactory(messageKey));
    }

    private sealed class InMemoryTrustStore : IRemoteImageSenderTrustStore
    {
        private readonly HashSet<(Guid AccountId, string Address)> _trusted = [];

        public Task<bool> IsTrustedAsync(
            Guid accountId,
            string normalizedSenderAddress,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_trusted.Contains((accountId, normalizedSenderAddress)));

        public Task TrustAsync(
            Guid accountId,
            string normalizedSenderAddress,
            CancellationToken cancellationToken = default)
        {
            _trusted.Add((accountId, normalizedSenderAddress));
            return Task.CompletedTask;
        }

        public Task RevokeAsync(
            Guid accountId,
            string normalizedSenderAddress,
            CancellationToken cancellationToken = default)
        {
            _trusted.Remove((accountId, normalizedSenderAddress));
            return Task.CompletedTask;
        }

        public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            _trusted.RemoveWhere(value => value.AccountId == accountId);
            return Task.CompletedTask;
        }
    }
}
