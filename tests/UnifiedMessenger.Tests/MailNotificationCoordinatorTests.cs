using System.Reflection;
using System.Text.Json;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class MailNotificationCoordinatorTests
{
    [Fact]
    public void NewMailInInactiveAccount_SetsRuntimeActivity()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 1);

        Assert.True(fixture.First.HasNewMailActivity);
    }

    [Fact]
    public void NewMailInInactiveAccount_ShowsOneSafeFallbackPopup()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 1);

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Gmail • first@example.test", popup.ServiceName);
        Assert.Equal("Новое письмо", popup.Title);
        Assert.Equal("Получено новое письмо", popup.Body);
        Assert.Equal(NotificationPopupBrand.Gmail, popup.Brand);
        Assert.Null(popup.PreviewText);
        Assert.Null(popup.SenderAvatarInitials);
    }

    [Fact]
    public void SingleGmailAccount_UsesSenderSubjectSnippetAndInitials()
    {
        using Fixture fixture = new(includeSecondAccount: false);

        fixture.Handle(
            fixture.First,
            1,
            new MailNotificationPreview(
                "Анна Смирнова",
                "anna@example.test",
                "Статус проекта",
                "Короткий безопасный preview"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Gmail", popup.ServiceName);
        Assert.Equal("Анна Смирнова", popup.Title);
        Assert.Equal("Статус проекта", popup.Body);
        Assert.Equal("Короткий безопасный preview", popup.PreviewText);
        Assert.Equal(NotificationPopupBrand.Gmail, popup.Brand);
        Assert.Equal(48d, popup.PreviewMaxHeight);
        Assert.Equal("АС", popup.SenderAvatarInitials);
        Assert.True(popup.ShowSenderInitials);
        Assert.False(popup.ShowContentSourceIcon);
    }

    [Fact]
    public void MultipleGmailAccounts_DisambiguateHeaderWithAccountIdentity()
    {
        using Fixture fixture = new();

        fixture.Handle(
            fixture.First,
            1,
            new MailNotificationPreview("Sender", "sender@example.test", "Subject", "Snippet"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Gmail • first@example.test", popup.ServiceName);
        Assert.Equal(fixture.First.Id, popup.ServiceInstanceId);
    }

    [Fact]
    public void SingleYandexAccount_UsesSenderSubjectAndYandexBranding()
    {
        using Fixture fixture = new(includeSecondAccount: false, provider: MailProviderType.Yandex);
        fixture.Handle(fixture.First, 1,
            new MailNotificationPreview("Иван Петров", "ivan@yandex.test", "Важное письмо", "Короткий preview"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Яндекс Почта", popup.ServiceName);
        Assert.Equal("Иван Петров", popup.Title);
        Assert.Equal("Важное письмо", popup.Body);
        Assert.Equal("Короткий preview", popup.PreviewText);
        Assert.Equal(NotificationPopupBrand.Yandex, popup.Brand);
        Assert.Equal(30d, popup.PreviewMaxHeight);
        Assert.False(popup.IsGmail);
        Assert.Equal("ИП", popup.SenderAvatarInitials);
        Assert.True(popup.ShowSenderInitials);
    }

    [Fact]
    public void MultipleYandexAccounts_IncludeAccountIdentity()
    {
        using Fixture fixture = new(provider: MailProviderType.Yandex);
        fixture.Handle(fixture.First, 1,
            new MailNotificationPreview("Sender", "sender@yandex.test", "Subject", string.Empty));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Яндекс Почта • first@example.test", popup.ServiceName);
        Assert.Equal(NotificationPopupBrand.Yandex, popup.Brand);
    }

    [Fact]
    public void YandexMissingSenderSubjectAndSnippet_UsesSafeFallbacks()
    {
        using Fixture fixture = new(includeSecondAccount: false, provider: MailProviderType.Yandex);
        fixture.Handle(fixture.First, 1,
            new MailNotificationPreview(string.Empty, string.Empty, string.Empty, string.Empty));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Неизвестный отправитель", popup.Title);
        Assert.Equal("(без темы)", popup.Body);
        Assert.Null(popup.PreviewText);
        Assert.Null(popup.SenderAvatarInitials);
        Assert.Equal(NotificationPopupBrand.Yandex, popup.Brand);
    }

    [Fact]
    public void YandexInitialsAvatarColor_IsDeterministicBySenderIdentity()
    {
        using Fixture fixture = new(includeSecondAccount: false, provider: MailProviderType.Yandex);
        MailNotificationPreview preview = new("Иван Петров", "ivan@yandex.test", "Subject", "Snippet");
        fixture.Handle(fixture.First, 1, preview);
        string first = Assert.Single(fixture.Popup.Shown).SenderAvatarBackground;
        fixture.Popup.Shown.Clear();
        fixture.Handle(fixture.First, 1, preview);
        Assert.Equal(first, Assert.Single(fixture.Popup.Shown).SenderAvatarBackground);
    }

    [Fact]
    public void MissingSenderName_UsesAddressAndGmailIconFallback()
    {
        using Fixture fixture = new(includeSecondAccount: false);

        fixture.Handle(
            fixture.First,
            1,
            new MailNotificationPreview(
                "sender@example.test",
                "sender@example.test",
                "Subject",
                "Snippet"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("sender@example.test", popup.Title);
        Assert.Null(popup.SenderAvatarInitials);
        Assert.False(popup.ShowSenderInitials);
        Assert.True(popup.ShowContentSourceIcon);
    }

    [Fact]
    public void MissingSenderSubjectAndSnippet_UsesSafeFallbacks()
    {
        using Fixture fixture = new(includeSecondAccount: false);

        fixture.Handle(
            fixture.First,
            1,
            new MailNotificationPreview(string.Empty, string.Empty, string.Empty, string.Empty));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Неизвестный отправитель", popup.Title);
        Assert.Equal("(без темы)", popup.Body);
        Assert.Null(popup.PreviewText);
        Assert.Null(popup.SenderAvatarInitials);
        Assert.True(popup.ShowContentSourceIcon);
    }

    [Fact]
    public void NotificationPreviewDisabled_KeepsGmailBrandButHidesMetadata()
    {
        using Fixture fixture = new(includeSecondAccount: false);
        fixture.Settings.Current.Notifications.ShowNotificationPreview = false;

        fixture.Handle(
            fixture.First,
            1,
            new MailNotificationPreview("Sender", "sender@example.test", "Subject", "Snippet"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Gmail", popup.ServiceName);
        Assert.Equal("Новое письмо", popup.Title);
        Assert.Equal("Получено новое письмо", popup.Body);
        Assert.Null(popup.PreviewText);
        Assert.Null(popup.SenderAvatarInitials);
        Assert.Equal(NotificationPopupBrand.Gmail, popup.Brand);
    }

    [Fact]
    public void NewMailInInactiveAccount_PlaysOneSharedLanternSound()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 1);

        Assert.Equal([ServiceType.Gmail], fixture.Sound.ServiceTypes);
    }

    [Fact]
    public void IndependentMailAccounts_DoNotSuppressEachOthersSound()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 1);
        fixture.Handle(fixture.Second, 1);

        Assert.Equal([ServiceType.Gmail, ServiceType.Gmail], fixture.Sound.ServiceTypes);
    }

    [Fact]
    public void NewMailInInactiveAccount_ProducesTaskbarActivity()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 1);

        Assert.True(ApplicationTrayCoordinator.HasTaskbarActivity([], [fixture.First]));
    }

    [Fact]
    public void BatchNotification_ShowsOnePopupAndPlaysOneSound()
    {
        using Fixture fixture = new();

        fixture.Handle(fixture.First, 4);

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Gmail • first@example.test", popup.ServiceName);
        Assert.Equal("Новые письма", popup.Title);
        Assert.Equal("Получено новых писем: 4", popup.Body);
        Assert.Null(popup.PreviewText);
        Assert.Single(fixture.Sound.ServiceTypes);
    }

    [Fact]
    public void SelectedExactActiveAccount_SuppressesPopupSoundAndActivity()
    {
        using Fixture fixture = new();
        fixture.Navigation.ActiveAccountId = fixture.First.Id;
        fixture.Navigation.IsMainWindowActive = true;

        fixture.Handle(fixture.First, 1);

        Assert.False(fixture.First.HasNewMailActivity);
        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.ServiceTypes);
        Assert.Equal([(fixture.First.Id, true)], fixture.Freshness.Detections);
    }

    [Fact]
    public void SelectedVisibleButUnfocusedGmail_RefreshesInboxAndKeepsNotificationPresentation()
    {
        using Fixture fixture = new();
        fixture.Navigation.ActiveAccountId = fixture.First.Id;
        fixture.Navigation.IsMainWindowVisible = true;
        fixture.Navigation.IsMainWindowActive = false;

        fixture.Handle(fixture.First, 1);

        Assert.Equal([(fixture.First.Id, true)], fixture.Freshness.Detections);
        Assert.True(fixture.First.HasNewMailActivity);
        Assert.Single(fixture.Popup.Shown);
        Assert.Single(fixture.Sound.ServiceTypes);
    }

    [Fact]
    public async Task SelectedLoadedInbox_NewMailRefreshesWithoutNavigationOrWindowFocus()
    {
        MailAccount account = Account();
        QueueInboxReadProvider readProvider = new(
            Page("cached"),
            Page("new"));
        using MailInboxViewModel inbox = new(new SingleReadProviderFactory(readProvider));
        await inbox.ActivateAsync(account);
        using FakePollingMonitor monitor = new();
        TestSettingsStore settings = new(new AppSettings { MailAccounts = [account] });
        MailActivityCoordinator activity = new();
        using FakePopupService popup = new();
        FakeSoundPlayer sound = new();
        FakeMailNavigation navigation = new()
        {
            ActiveAccountId = account.Id,
            IsMainWindowVisible = true,
            IsMainWindowActive = false
        };
        using MailNotificationCoordinator coordinator = new(
            monitor,
            settings,
            activity,
            popup,
            sound,
            navigation,
            inbox);

        monitor.Raise(new MailNewMessageDetectedEventArgs(account.Id, 1));
        await inbox.GetCurrentInboxRefreshTask(account.Id);

        Assert.Equal(2, readProvider.PageCallCount);
        Assert.Equal("new", Assert.Single(inbox.Messages).MessageKey);
        Assert.False(inbox.IsInboxStale(account.Id));
        Assert.Equal(account.Id, navigation.ActiveAccountId);
        Assert.Single(popup.Shown);
        Assert.Single(sound.ServiceTypes);
    }

    [Fact]
    public void SelectedDifferentActiveAccount_AllowsPopupSoundAndActivity()
    {
        using Fixture fixture = new();
        fixture.Navigation.ActiveAccountId = fixture.Second.Id;
        fixture.Navigation.IsMainWindowActive = true;

        fixture.Handle(fixture.First, 1);

        Assert.True(fixture.First.HasNewMailActivity);
        Assert.Single(fixture.Popup.Shown);
        Assert.Single(fixture.Sound.ServiceTypes);
    }

    [Fact]
    public void ClearingOpenedAccountActivity_DoesNotClearServerUnreadCount()
    {
        using Fixture fixture = new();
        fixture.First.InboxUnreadCount = 23;
        fixture.Handle(fixture.First, 1);

        fixture.Activity.Clear(fixture.First);

        Assert.False(fixture.First.HasNewMailActivity);
        Assert.Equal(23, fixture.First.InboxUnreadCount);
    }

    [Fact]
    public void OpeningOneAccount_ClearsOnlyThatAccountsRuntimeActivity()
    {
        MailAccount first = Account();
        MailAccount second = Account();
        MailActivityCoordinator activity = new();
        activity.MarkNewMail(first);
        activity.MarkNewMail(second);

        activity.Clear(first);

        Assert.False(first.HasNewMailActivity);
        Assert.True(second.HasNewMailActivity);
    }

    [Fact]
    public void ServerUnreadCountAndRuntimeActivity_AreIndependentAndActivityIsNotSerialized()
    {
        MailAccount account = Account();
        account.InboxUnreadCount = 8;
        account.HasNewMailActivity = true;

        account.InboxUnreadCount = 3;

        Assert.True(account.HasNewMailActivity);
        Assert.DoesNotContain("HasNewMailActivity", JsonSerializer.Serialize(account), StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationsDisabled_SuppressesPopupAndSoundButKeepsUnreadAndActivity()
    {
        using Fixture fixture = new();
        fixture.Settings.Current.Notifications.IsEnabled = false;
        fixture.First.InboxUnreadCount = 11;

        fixture.Handle(fixture.First, 1);

        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.ServiceTypes);
        Assert.True(fixture.First.HasNewMailActivity);
        Assert.Equal(11, fixture.First.InboxUnreadCount);
    }

    [Fact]
    public void SoundDisabled_AllowsPopupWithoutSound()
    {
        using Fixture fixture = new();
        fixture.Settings.Current.Notifications.PlaySound = false;

        fixture.Handle(fixture.First, 1);

        Assert.Single(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.ServiceTypes);
        Assert.True(fixture.First.HasNewMailActivity);
    }

    [Fact]
    public void DoNotDisturb_SuppressesPopupAndSoundButKeepsActivity()
    {
        using Fixture fixture = new();
        fixture.Settings.Current.Notifications.DoNotDisturb = true;

        fixture.Handle(fixture.First, 1);

        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.ServiceTypes);
        Assert.True(fixture.First.HasNewMailActivity);
    }

    [Fact]
    public void WebActivityOrMailActivity_EnablesTaskbarOverlay()
    {
        ServiceInstance web = new() { IsEnabled = true };
        MailAccount mail = Account();

        web.HasUnreadActivity = true;
        Assert.True(ApplicationTrayCoordinator.HasTaskbarActivity([web], [mail]));

        web.HasUnreadActivity = false;
        mail.HasNewMailActivity = true;
        Assert.True(ApplicationTrayCoordinator.HasTaskbarActivity([web], [mail]));
    }

    [Fact]
    public void ClearingMailActivityWhileWebActivityRemains_KeepsTaskbarOverlay()
    {
        ServiceInstance web = new() { IsEnabled = true, HasUnreadActivity = true };
        MailAccount mail = Account();
        mail.HasNewMailActivity = true;
        MailActivityCoordinator activity = new();

        activity.Clear(mail);

        Assert.True(ApplicationTrayCoordinator.HasTaskbarActivity([web], [mail]));
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    public void PopupClick_ClearsActivityAndOpensCorrectAccountInbox(MailProviderType provider)
    {
        using Fixture fixture = new();
        fixture.First.Provider = provider;
        fixture.First.InboxUnreadCount = 14;
        fixture.Handle(fixture.First, 1);
        Guid popupId = Assert.Single(fixture.Popup.Shown).NotificationId;

        fixture.Popup.RaiseClicked(popupId);

        Assert.False(fixture.First.HasNewMailActivity);
        Assert.Equal(14, fixture.First.InboxUnreadCount);
        Assert.Equal([fixture.First.Id], fixture.Freshness.RequiredAccountIds);
        Assert.Equal([fixture.First.Id], fixture.Navigation.OpenedInboxAccountIds);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    public void InactiveDetection_ReusesSignalForInboxFreshnessWithoutChangingPresentation(MailProviderType provider)
    {
        using Fixture fixture = new();
        fixture.First.Provider = provider;

        fixture.Handle(fixture.First, 1);

        Assert.Equal([(fixture.First.Id, false)], fixture.Freshness.Detections);
        Assert.Single(fixture.Popup.Shown);
        Assert.Single(fixture.Sound.ServiceTypes);
        Assert.True(fixture.First.HasNewMailActivity);
    }

    [Fact]
    public void PublicDetectionContract_ContainsOnlyApprovedPreviewMetadataAndNoMessageIdentity()
    {
        PropertyInfo[] properties = typeof(MailNewMessageDetectedEventArgs).GetProperties();

        Assert.Equal(
            [
                nameof(MailNewMessageDetectedEventArgs.MailAccountId),
                nameof(MailNewMessageDetectedEventArgs.NewMessageCount),
                nameof(MailNewMessageDetectedEventArgs.Preview)
            ],
            properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(string));

        Assert.Equal(
            [
                nameof(MailNotificationPreview.SenderAddress),
                nameof(MailNotificationPreview.SenderDisplayName),
                nameof(MailNotificationPreview.Snippet),
                nameof(MailNotificationPreview.Subject)
            ],
            typeof(MailNotificationPreview)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    private static MailAccount Account(
        string emailAddress = "mail@example.test",
        MailProviderType provider = MailProviderType.Gmail) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        EmailAddress = emailAddress,
        IsEnabled = true,
        CredentialKey = "credential"
    };

    private static MailPage<MailMessageSummary> Page(string key) =>
        new(
            [new MailMessageSummary(
                key,
                $"Subject {key}",
                "Sender",
                "sender@example.test",
                DateTimeOffset.UtcNow,
                "Preview",
                true)],
            null);

    private sealed class Fixture : IDisposable
    {
        private readonly FakePollingMonitor _monitor = new();

        public Fixture(bool includeSecondAccount = true, MailProviderType provider = MailProviderType.Gmail)
        {
            First = Account("first@example.test", provider);
            Second = Account("second@example.test", provider);
            Settings = new TestSettingsStore(new AppSettings
            {
                MailAccounts = includeSecondAccount ? [First, Second] : [First]
            });
            Activity = new MailActivityCoordinator();
            Popup = new FakePopupService();
            Sound = new FakeSoundPlayer();
            Navigation = new FakeMailNavigation();
            Freshness = new FakeInboxFreshnessService();
            Coordinator = new MailNotificationCoordinator(
                _monitor,
                Settings,
                Activity,
                Popup,
                Sound,
                Navigation,
                Freshness);
        }

        public MailAccount First { get; }
        public MailAccount Second { get; }
        public TestSettingsStore Settings { get; }
        public MailActivityCoordinator Activity { get; }
        public FakePopupService Popup { get; }
        public FakeSoundPlayer Sound { get; }
        public FakeMailNavigation Navigation { get; }
        public FakeInboxFreshnessService Freshness { get; }
        public MailNotificationCoordinator Coordinator { get; }

        public void Handle(
            MailAccount account,
            int count,
            MailNotificationPreview? preview = null) =>
            _monitor.Raise(new MailNewMessageDetectedEventArgs(account.Id, count, preview));

        public void Dispose()
        {
            Coordinator.Dispose();
            Popup.Dispose();
            _monitor.Dispose();
        }
    }

    private sealed class FakeInboxFreshnessService : IMailInboxFreshnessService
    {
        public List<(Guid AccountId, bool IsActivelyViewed)> Detections { get; } = [];
        public List<Guid> RequiredAccountIds { get; } = [];

        public void OnNewMailDetected(Guid mailAccountId, bool isAccountActivelyViewed) =>
            Detections.Add((mailAccountId, isAccountActivelyViewed));

        public void RequireFreshInbox(Guid mailAccountId) =>
            RequiredAccountIds.Add(mailAccountId);
    }

    private sealed class SingleReadProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class QueueInboxReadProvider(params MailPage<MailMessageSummary>[] pages) : IMailReadProvider
    {
        private readonly Queue<MailPage<MailMessageSummary>> _pages = new(pages);

        public int PageCallCount { get; private set; }
        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageCallCount++;
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePollingMonitor : IMailBackgroundPollingMonitor
    {
        public event EventHandler<MailNewMessageDetectedEventArgs>? MailNewMessageDetected;
        public void Raise(MailNewMessageDetectedEventArgs eventArgs) =>
            MailNewMessageDetected?.Invoke(this, eventArgs);
        public void Start() { }
        public void BeginShutdown() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() => MailNewMessageDetected = null;
    }

    private sealed class FakeMailNavigation : IMailNotificationNavigation
    {
        public Guid? ActiveAccountId { get; set; }
        public bool IsMainWindowActive { get; set; }
        public bool IsMainWindowVisible { get; set; } = true;
        public List<Guid> OpenedInboxAccountIds { get; } = [];

        public bool IsAccountActivelyViewed(Guid mailAccountId) =>
            IsMainWindowActive && ActiveAccountId == mailAccountId;

        public bool IsAccountSelectedInMailUi(Guid mailAccountId) =>
            IsMainWindowVisible && ActiveAccountId == mailAccountId;

        public void OpenInbox(Guid mailAccountId) => OpenedInboxAccountIds.Add(mailAccountId);
    }

    private sealed class FakePopupService : INotificationPopupService
    {
        public event EventHandler<NotificationPopupEventArgs>? Clicked;
        public event EventHandler<NotificationPopupEventArgs>? Closed;
        public List<NotificationPopupDisplayModel> Shown { get; } = [];
        public int VisibleCount => Shown.Count;

        public bool TryShow(NotificationPopupDisplayModel notification)
        {
            Shown.Add(notification);
            return true;
        }

        public void Close(Guid notificationId) =>
            Closed?.Invoke(this, new NotificationPopupEventArgs(notificationId));

        public void CloseAll()
        {
            foreach (Guid id in Shown.Select(notification => notification.NotificationId).ToArray())
            {
                Close(id);
            }
        }

        public void RaiseClicked(Guid notificationId) =>
            Clicked?.Invoke(this, new NotificationPopupEventArgs(notificationId));

        public void Dispose()
        {
            Clicked = null;
            Closed = null;
        }
    }

    private sealed class FakeSoundPlayer : INotificationSoundPlayer
    {
        public List<ServiceType> ServiceTypes { get; } = [];

        public bool TryPlay(ServiceType serviceType)
        {
            ServiceTypes.Add(serviceType);
            return true;
        }

        public bool TryPreviewLanternSound() => true;
    }

    private sealed class TestSettingsStore(AppSettings settings) : IApplicationSettingsStore
    {
        public AppSettings Current { get; private set; } = settings;
        public bool IsInitialized => true;
        public void Initialize(AppSettings value) => Current = value;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
