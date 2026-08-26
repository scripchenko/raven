using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class StartupMailUnreadRefreshTests
{
    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task EnabledAccount_RequestsInboxUnreadCountExactlyOnce(MailProviderType providerType)
    {
        ProbeMailProvider provider = new() { UnreadCount = 5 };
        MailAccount account = Account(providerType);
        StartupMailUnreadRefreshService service = CreateService(provider);

        await service.RefreshAsync([account]);

        Assert.Equal(1, provider.UnreadCountRequests);
        Assert.Equal(5, account.InboxUnreadCount);
        Assert.Equal("5", account.UnreadBadgeText);
    }

    [Fact]
    public async Task DisabledAccount_IsNotRequested()
    {
        ProbeMailProvider provider = new() { UnreadCount = 5 };
        MailAccount account = Account(MailProviderType.Gmail);
        account.IsEnabled = false;

        await CreateService(provider).RefreshAsync([account]);

        Assert.Equal(0, provider.UnreadCountRequests);
        Assert.Null(account.InboxUnreadCount);
    }

    [Fact]
    public async Task ProviderFailure_IsolatedAndOtherAccountsStillUpdate()
    {
        MailAccount failing = Account(MailProviderType.Gmail);
        MailAccount yandex = Account(MailProviderType.Yandex);
        MailAccount mailRu = Account(MailProviderType.MailRu);
        ProbeMailProvider provider = new()
        {
            UnreadCountByAccount = account => account.Id == failing.Id
                ? throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Unavailable")
                : account.Id == yandex.Id ? 3 : 7
        };

        await CreateService(provider).RefreshAsync([failing, yandex, mailRu]);

        Assert.Null(failing.InboxUnreadCount);
        Assert.Equal(3, yandex.InboxUnreadCount);
        Assert.Equal(7, mailRu.InboxUnreadCount);
        Assert.Equal(3, provider.UnreadCountRequests);
    }

    [Fact]
    public async Task Refresh_IsMetadataOnlyAndDoesNotChangeNavigationState()
    {
        ProbeMailProvider provider = new() { UnreadCount = 2 };
        MailAccount gmail = Account(MailProviderType.Gmail);
        Guid selectedNavigationId = Guid.NewGuid();
        Guid selectedBefore = selectedNavigationId;

        await CreateService(provider).RefreshAsync([gmail]);

        Assert.Equal(selectedBefore, selectedNavigationId);
        Assert.Equal(0, provider.PageRequests);
        Assert.Equal(0, provider.MessageRequests);
        Assert.Equal(0, provider.AttachmentRequests);
        Assert.Equal(
            [typeof(IMailReadProviderFactory), typeof(IUiDispatcher)],
            typeof(StartupMailUnreadRefreshService)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
    }

    [Fact]
    public async Task Refresh_UsesBoundedConcurrency()
    {
        ConcurrencyProbe concurrency = new();
        ProbeMailProvider provider = new()
        {
            GetUnreadCountAsync = async (_, cancellationToken) =>
            {
                int active = Interlocked.Increment(ref concurrency.Active);
                UpdateMaximum(concurrency, active);
                try
                {
                    await Task.Delay(40, cancellationToken);
                    return 1;
                }
                finally
                {
                    Interlocked.Decrement(ref concurrency.Active);
                }
            }
        };
        MailAccount[] accounts = Enumerable.Range(0, 6)
            .Select(_ => Account(MailProviderType.GenericImap))
            .ToArray();

        await CreateService(provider).RefreshAsync(accounts);

        Assert.InRange(concurrency.Maximum, 1, StartupMailUnreadRefreshService.MaximumConcurrency);
        Assert.Equal(6, provider.UnreadCountRequests);
    }

    [Fact]
    public async Task Refresh_IsOneShotAndCreatesNoPollingTimer()
    {
        ProbeMailProvider provider = new() { UnreadCount = 1 };

        await CreateService(provider).RefreshAsync([Account(MailProviderType.Gmail)]);
        await Task.Delay(75);

        Assert.Equal(1, provider.UnreadCountRequests);
        Assert.DoesNotContain(
            typeof(StartupMailUnreadRefreshService).GetFields(
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public),
            field => field.FieldType == typeof(Timer)
                || field.FieldType == typeof(PeriodicTimer));
    }

    [Fact]
    public async Task StartupCount_ContinuesThroughExistingReadMutationSemantics()
    {
        ProbeMailProvider provider = new() { UnreadCount = 5, MessageIsUnread = true };
        MailAccount gmail = Account(MailProviderType.Gmail);
        await CreateService(provider).RefreshAsync([gmail]);
        using MailInboxViewModel inbox = new(new ProbeProviderFactory(provider));

        await inbox.ActivateAsync(gmail);
        inbox.SelectedMessageSummary = Assert.Single(inbox.Messages);
        await inbox.CurrentMessageLoadTask;
        await inbox.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal(4, gmail.InboxUnreadCount);
        Assert.Equal("4", gmail.UnreadBadgeText);
    }

    [Fact]
    public void App_StartsOneShotRefreshAfterMainWindowIsShown()
    {
        string source = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "App.xaml.cs"));
        int showIndex = source.IndexOf("window.Show();", StringComparison.Ordinal);
        int refreshIndex = source.IndexOf(
            "StartStartupMailUnreadRefresh(loadResult.Settings.MailAccounts);",
            StringComparison.Ordinal);

        Assert.True(showIndex >= 0);
        Assert.True(refreshIndex > showIndex);
        Assert.Equal(
            1,
            source.Split(
                "StartStartupMailUnreadRefresh(loadResult.Settings.MailAccounts);",
                StringSplitOptions.None).Length - 1);
    }

    private static StartupMailUnreadRefreshService CreateService(ProbeMailProvider provider) =>
        new(new ProbeProviderFactory(provider), new ImmediateDispatcher());

    private static MailAccount Account(MailProviderType provider) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        EmailAddress = "mail@example.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

    private static void UpdateMaximum(ConcurrencyProbe probe, int active)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref probe.Maximum);
            if (active <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref probe.Maximum, active, observed) != observed);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class ConcurrencyProbe
    {
        public int Active;
        public int Maximum;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private sealed class ProbeProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class ProbeMailProvider :
        IMailReadProvider,
        IMailMessageStateProvider,
        IMailInboxUnreadCountProvider,
        IMailAttachmentContentProvider
    {
        public int UnreadCount { get; set; }
        public bool MessageIsUnread { get; set; } = true;
        public Func<MailAccount, int>? UnreadCountByAccount { get; set; }
        public Func<MailAccount, CancellationToken, Task<int>>? GetUnreadCountAsync { get; set; }
        private int _unreadCountRequests;

        public int UnreadCountRequests => Volatile.Read(ref _unreadCountRequests);
        public int PageRequests { get; private set; }
        public int MessageRequests { get; private set; }
        public int AttachmentRequests { get; private set; }

        public bool Supports(MailProviderType providerType) => true;

        public async Task<int> GetInboxUnreadCountAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _unreadCountRequests);
            if (GetUnreadCountAsync is not null)
            {
                return await GetUnreadCountAsync(account, cancellationToken);
            }

            return UnreadCountByAccount?.Invoke(account) ?? UnreadCount;
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageRequests++;
            return Task.FromResult(new MailPage<MailMessageSummary>([Summary(account)], null));
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            MessageRequests++;
            return Task.FromResult(new MailMessageContent(
                messageKey,
                "Subject",
                "Sender",
                "sender@example.test",
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "Body",
                [],
                MessageIsUnread,
                false));
        }

        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(
            MailAccount account,
            MailFolder folder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MailReadStateCapability.Available);

        public Task SetReadStateAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            bool newUnread = !isRead;
            if (MessageIsUnread != newUnread)
            {
                UnreadCount = Math.Max(0, UnreadCount + (newUnread ? 1 : -1));
                MessageIsUnread = newUnread;
            }

            return Task.CompletedTask;
        }

        public Task<MailAttachmentContent> GetAsync(
            MailAccount account,
            string messageKey,
            string attachmentKey,
            CancellationToken cancellationToken = default)
        {
            AttachmentRequests++;
            throw new InvalidOperationException("Startup refresh must not fetch attachments.");
        }

        private MailMessageSummary Summary(MailAccount account) => new(
            $"message:{account.Id:N}",
            "Subject",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            MessageIsUnread);
    }
}
