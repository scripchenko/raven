using System.Text.Json;
using System.Xml.Linq;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class SidebarBadgeTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(5, "5")]
    [InlineData(9, "9")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1240, "99+")]
    public void Formatter_UsesUnifiedZeroTo99PlusRules(int count, string? expected)
    {
        Assert.Equal(expected, SidebarBadgeFormatter.Format(count));
    }

    [Fact]
    public void MainWindow_UsesOneSharedTopRightBadgeAndNoDotImplementation()
    {
        string path = FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "MainWindow.xaml");
        string source = File.ReadAllText(path);
        XDocument document = XDocument.Parse(source);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement style = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute(x + "Key") == "SidebarUnreadBadgeStyle");
        Dictionary<string, string?> setters = style.Elements(presentation + "Setter")
            .ToDictionary(
                element => (string)element.Attribute("Property")!,
                element => (string?)element.Attribute("Value"),
                StringComparer.Ordinal);

        Assert.Equal("Right", setters["HorizontalAlignment"]);
        Assert.Equal("Top", setters["VerticalAlignment"]);
        Assert.Equal("#E53935", setters["Background"]);
        Assert.Single(
            document.Descendants(presentation + "Border"),
            element => ((string?)element.Attribute("Style"))?.Contains(
                "SidebarUnreadBadgeStyle",
                StringComparison.Ordinal) == true);
        Assert.DoesNotContain("ShowUnreadDot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service-specific badge", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebLocalActivityCounts_AreIsolatedAndClearOnlyViewedService()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance telegram = Service(ServiceType.Telegram);
        ServiceInstance whatsapp = Service(ServiceType.WhatsApp);
        ServiceInstance max = Service(ServiceType.Max);
        ServiceInstance vk = Service(ServiceType.VkMessenger);

        coordinator.MarkNotificationReceived(telegram);
        coordinator.MarkNotificationReceived(telegram);
        coordinator.MarkNotificationReceived(whatsapp);
        coordinator.MarkNotificationReceived(max);
        coordinator.MarkNotificationReceived(vk);

        Assert.Equal(2, telegram.LanternUnviewedActivityCount);
        Assert.Equal(1, whatsapp.LanternUnviewedActivityCount);
        Assert.Equal(1, max.LanternUnviewedActivityCount);
        Assert.Equal(1, vk.LanternUnviewedActivityCount);
        Assert.Null(telegram.UnreadCount);

        coordinator.Clear(whatsapp);

        Assert.Equal(2, telegram.LanternUnviewedActivityCount);
        Assert.Equal(0, whatsapp.LanternUnviewedActivityCount);
        Assert.Equal(1, max.LanternUnviewedActivityCount);
        Assert.Equal(1, vk.LanternUnviewedActivityCount);
    }

    [Fact]
    public async Task MailInboxUnreadCount_UpdatesAfterMutationsAndRemainsAccountIsolated()
    {
        BadgeMailProvider provider = new();
        MailAccount gmail = Account(Guid.Parse("11111111-1111-1111-1111-111111111111"), MailProviderType.Gmail);
        MailAccount yandex = Account(Guid.Parse("22222222-2222-2222-2222-222222222222"), MailProviderType.Yandex);
        provider.Configure(gmail.Id, 4, isUnread: true);
        provider.Configure(yandex.Id, 7, isUnread: true);
        using MailInboxViewModel viewModel = new(new BadgeProviderFactory(provider));

        await viewModel.ActivateAsync(gmail);
        Assert.Equal(4, gmail.InboxUnreadCount);
        Assert.Equal(0, provider.MessageBodyFetches);
        using NavigationAccountItem gmailNavigation = NavigationAccountItem.FromMail(gmail);
        Assert.Equal("4", gmailNavigation.UnreadBadgeText);
        viewModel.SelectedMessageSummary = Assert.Single(viewModel.Messages);
        await viewModel.CurrentMessageLoadTask;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        Assert.Equal(3, gmail.InboxUnreadCount);
        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        Assert.Equal(4, gmail.InboxUnreadCount);

        int bodyFetchesBeforeAccountSwitch = provider.MessageBodyFetches;
        await viewModel.ActivateAsync(yandex);
        Assert.Equal(7, yandex.InboxUnreadCount);
        Assert.Equal(4, gmail.InboxUnreadCount);
        Assert.Equal(bodyFetchesBeforeAccountSwitch, provider.MessageBodyFetches);
        viewModel.SelectedMessageSummary = Assert.Single(viewModel.Messages);
        await viewModel.CurrentMessageLoadTask;
        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal(6, yandex.InboxUnreadCount);
        Assert.Equal(4, gmail.InboxUnreadCount);
    }

    [Fact]
    public void RuntimeBadgeState_IsNeverSerialized()
    {
        ServiceInstance service = Service(ServiceType.Telegram);
        service.LanternUnviewedActivityCount = 8;
        service.UnreadCount = 12;
        MailAccount mail = Account(Guid.NewGuid(), MailProviderType.MailRu);
        mail.InboxUnreadCount = 23;
        AppSettings settings = new() { Services = [service], MailAccounts = [mail] };

        string json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("LanternUnviewedActivityCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain("InboxUnreadCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UnreadCount", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionProviders_UseLightweightInboxMetadataInsteadOfMessageBodies()
    {
        string gmail = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Mail", "GmailMailReadProvider.cs"));
        string imap = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Mail", "ImapMailReadProvider.cs"));

        Assert.Contains("Users.Labels.Get(\"me\", GmailSystemFolders.Inbox)", gmail, StringComparison.Ordinal);
        Assert.Contains("MessagesUnread", gmail, StringComparison.Ordinal);
        Assert.Contains("StatusAsync(StatusItems.Unread", imap, StringComparison.Ordinal);
    }

    private static ServiceInstance Service(ServiceType type) => new()
    {
        Id = Guid.NewGuid(),
        ServiceType = type,
        IsEnabled = true
    };

    private static MailAccount Account(Guid id, MailProviderType provider) => new()
    {
        Id = id,
        Provider = provider,
        EmailAddress = "mail@example.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

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

    private sealed class BadgeProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class BadgeMailProvider : IMailReadProvider, IMailMessageStateProvider, IMailInboxUnreadCountProvider
    {
        private readonly Dictionary<Guid, int> _counts = [];
        private readonly Dictionary<Guid, bool> _unread = [];

        public int MessageBodyFetches { get; private set; }

        public void Configure(Guid accountId, int count, bool isUnread)
        {
            _counts[accountId] = count;
            _unread[accountId] = isUnread;
        }

        public bool Supports(MailProviderType providerType) => true;

        public Task<int> GetInboxUnreadCountAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_counts[account.Id]);

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([MailFolderCatalog.Inbox()]);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([Summary(account)], null));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            MessageBodyFetches++;
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
                _unread[account.Id],
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
            if (_unread[account.Id] != newUnread)
            {
                _counts[account.Id] = Math.Max(0, _counts[account.Id] + (newUnread ? 1 : -1));
                _unread[account.Id] = newUnread;
            }

            return Task.CompletedTask;
        }

        private MailMessageSummary Summary(MailAccount account) => new(
            $"message:{account.Id:N}",
            "Subject",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            _unread[account.Id]);
    }
}
