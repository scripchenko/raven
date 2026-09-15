using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.App.Services.Notifications;

public interface IMailNotificationCoordinator : IDisposable
{
    void OnDoNotDisturbChanged(bool enabled);
    void Shutdown();
}

public interface IMailNotificationNavigation
{
    bool IsAccountActivelyViewed(Guid mailAccountId);
    bool IsAccountSelectedInMailUi(Guid mailAccountId);
    void OpenInbox(Guid mailAccountId);
}

public sealed class MailNotificationNavigation(
    IWindowActivationService windowActivation,
    MainWindowViewModel mainWindowViewModel,
    MailInboxViewModel mailInboxViewModel) : IMailNotificationNavigation
{
    public bool IsAccountActivelyViewed(Guid mailAccountId) =>
        !mainWindowViewModel.IsSettingsOpen
        && windowActivation.IsMainWindowActive
        && mainWindowViewModel.SelectedMailAccount?.Id == mailAccountId;

    public bool IsAccountSelectedInMailUi(Guid mailAccountId) =>
        !mainWindowViewModel.IsSettingsOpen
        && windowActivation.IsMainWindowVisible
        && mainWindowViewModel.SelectedMailAccount?.Id == mailAccountId;

    public void OpenInbox(Guid mailAccountId)
    {
        mailInboxViewModel.OpenInbox(mailAccountId);
        mainWindowViewModel.SelectMailAccount(mailAccountId);
        windowActivation.ShowAndActivate();
    }
}

public sealed class MailNotificationCoordinator : IMailNotificationCoordinator
{
    internal const int MaximumPendingNotifications = 20;

    private readonly Queue<PendingMailNotification> _pending = new();
    private readonly Dictionary<Guid, ActiveMailNotification> _active = [];
    private readonly IMailBackgroundPollingMonitor _pollingMonitor;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IMailActivityCoordinator _activityCoordinator;
    private readonly INotificationPopupService _popupService;
    private readonly INotificationSoundPlayer _soundPlayer;
    private readonly IMailNotificationNavigation _navigation;
    private readonly IMailInboxFreshnessService _inboxFreshness;
    private bool _shutdown;
    private bool _disposed;

    public MailNotificationCoordinator(
        IMailBackgroundPollingMonitor pollingMonitor,
        IApplicationSettingsStore settingsStore,
        IMailActivityCoordinator activityCoordinator,
        INotificationPopupService popupService,
        INotificationSoundPlayer soundPlayer,
        IMailNotificationNavigation navigation,
        IMailInboxFreshnessService inboxFreshness)
    {
        _pollingMonitor = pollingMonitor;
        _settingsStore = settingsStore;
        _activityCoordinator = activityCoordinator;
        _popupService = popupService;
        _soundPlayer = soundPlayer;
        _navigation = navigation;
        _inboxFreshness = inboxFreshness;
        _pollingMonitor.MailNewMessageDetected += OnMailNewMessageDetected;
        _popupService.Clicked += OnPopupClicked;
        _popupService.Closed += OnPopupClosed;
    }

    internal int PendingCount => _pending.Count;
    internal int ActiveCount => _active.Count;

    public void OnDoNotDisturbChanged(bool enabled)
    {
        if (enabled)
        {
            ClearNotifications();
        }
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        ClearNotifications();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
        _pollingMonitor.MailNewMessageDetected -= OnMailNewMessageDetected;
        _popupService.Clicked -= OnPopupClicked;
        _popupService.Closed -= OnPopupClosed;
    }

    internal void Handle(MailNewMessageDetectedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (_shutdown || FindEnabledAccount(eventArgs.MailAccountId) is not MailAccount account)
        {
            return;
        }

        bool isSelectedAndActive = IsSelectedAndActive(account);
        if (account.Provider is MailProviderType.Gmail or MailProviderType.Yandex)
        {
            _inboxFreshness.OnNewMailDetected(
                account.Id,
                _navigation.IsAccountSelectedInMailUi(account.Id));
        }

        if (isSelectedAndActive)
        {
            return;
        }

        _activityCoordinator.MarkNewMail(account);
        NotificationSettings notifications = _settingsStore.Current.Notifications;
        if (!notifications.IsEnabled || notifications.DoNotDisturb)
        {
            return;
        }

        if (notifications.PlaySound)
        {
            _ = _soundPlayer.TryPlay(ServiceType.Gmail);
        }

        Enqueue(new PendingMailNotification(
            account.Id,
            eventArgs.NewMessageCount,
            eventArgs.Preview,
            notifications.ShowNotificationPreview));
        TryShowAvailable();
    }

    private void OnMailNewMessageDetected(object? sender, MailNewMessageDetectedEventArgs eventArgs) =>
        Handle(eventArgs);

    private void Enqueue(PendingMailNotification notification)
    {
        if (_pending.Count >= MaximumPendingNotifications)
        {
            _ = _pending.Dequeue();
        }

        _pending.Enqueue(notification);
    }

    private void TryShowAvailable()
    {
        if (_shutdown)
        {
            return;
        }

        while (_pending.TryDequeue(out PendingMailNotification? pending))
        {
            MailAccount? account = FindEnabledAccount(pending.MailAccountId);
            NotificationSettings notifications = _settingsStore.Current.Notifications;
            if (account is null
                || !notifications.IsEnabled
                || notifications.DoNotDisturb
                || IsSelectedAndActive(account))
            {
                continue;
            }

            Guid notificationId = Guid.NewGuid();
            NotificationPopupDisplayModel popup = CreatePopupModel(
                notificationId,
                account,
                pending,
                HasMultipleEnabledGmailAccounts());
            if (_popupService.TryShow(popup))
            {
                _active.Add(
                    notificationId,
                    new ActiveMailNotification(notificationId, pending.MailAccountId));
                continue;
            }

            _pending.Enqueue(pending);
            return;
        }
    }

    internal static NotificationPopupDisplayModel CreatePopupModel(
        Guid notificationId,
        MailAccount account,
        PendingMailNotification pending,
        bool hasMultipleEnabledGmailAccounts)
    {
        bool isSingle = pending.NewMessageCount == 1;
        string serviceName = account.Provider is MailProviderType.Gmail
            ? CreateGmailServiceName(account, hasMultipleEnabledGmailAccounts)
            : "Почта";
        if (account.Provider is MailProviderType.Gmail
            && isSingle
            && pending.Preview is not null
            && pending.ShowPreview)
        {
            string senderName = pending.Preview.SenderDisplayName.Trim();
            string senderAddress = pending.Preview.SenderAddress.Trim();
            string title = !string.IsNullOrWhiteSpace(senderName)
                ? senderName
                : !string.IsNullOrWhiteSpace(senderAddress)
                    ? senderAddress
                    : "Неизвестный отправитель";
            string subject = MailContentExtractor.NormalizeSubject(pending.Preview.Subject);
            string snippet = MailContentExtractor.NormalizePreview(pending.Preview.Snippet);
            return new NotificationPopupDisplayModel(
                notificationId,
                pending.MailAccountId,
                serviceName,
                title,
                subject,
                string.IsNullOrWhiteSpace(snippet) ? null : snippet,
                NotificationPopupBrand.Gmail,
                CreateSenderInitials(senderName, senderAddress));
        }

        return new NotificationPopupDisplayModel(
            notificationId,
            pending.MailAccountId,
            serviceName,
            isSingle ? "Новое письмо" : "Новые письма",
            isSingle
                ? "Получено новое письмо"
                : $"Получено новых писем: {pending.NewMessageCount}",
            Brand: account.Provider is MailProviderType.Gmail
                ? NotificationPopupBrand.Gmail
                : NotificationPopupBrand.Default);
    }

    internal static string? CreateSenderInitials(string? senderName, string? senderAddress)
    {
        string candidate = senderName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || string.Equals(candidate, senderAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            || candidate.Contains('@'))
        {
            return null;
        }

        string[] words = candidate
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        char first = char.ToUpperInvariant(words[0][0]);
        return words.Length == 1
            ? first.ToString()
            : string.Concat(first, char.ToUpperInvariant(words[^1][0]));
    }

    private static string CreateGmailServiceName(
        MailAccount account,
        bool hasMultipleEnabledGmailAccounts)
    {
        string identity = account.DisplayLabel.Trim();
        return hasMultipleEnabledGmailAccounts && !string.IsNullOrWhiteSpace(identity)
            ? $"Gmail • {identity}"
            : "Gmail";
    }

    private bool HasMultipleEnabledGmailAccounts() =>
        _settingsStore.Current.MailAccounts.Count(account =>
            account.IsEnabled && account.Provider is MailProviderType.Gmail) > 1;

    private void OnPopupClicked(object? sender, NotificationPopupEventArgs eventArgs)
    {
        if (!_active.Remove(eventArgs.NotificationId, out ActiveMailNotification? active))
        {
            TryShowAvailable();
            return;
        }

        MailAccount? account = FindEnabledAccount(active.MailAccountId);
        if (account is not null)
        {
            _activityCoordinator.Clear(account);
            if (account.Provider is MailProviderType.Gmail or MailProviderType.Yandex)
            {
                _inboxFreshness.RequireFreshInbox(account.Id);
            }

            _navigation.OpenInbox(account.Id);
        }

        TryShowAvailable();
    }

    private void OnPopupClosed(object? sender, NotificationPopupEventArgs eventArgs)
    {
        _active.Remove(eventArgs.NotificationId);
        TryShowAvailable();
    }

    private void ClearNotifications()
    {
        _pending.Clear();
        foreach (Guid notificationId in _active.Keys.ToArray())
        {
            _active.Remove(notificationId);
            _popupService.Close(notificationId);
        }
    }

    private bool IsSelectedAndActive(MailAccount account) =>
        _navigation.IsAccountActivelyViewed(account.Id);

    private MailAccount? FindEnabledAccount(Guid accountId) =>
        _settingsStore.Current.MailAccounts.FirstOrDefault(
            account => account.Id == accountId && account.IsEnabled);

    internal sealed record PendingMailNotification(
        Guid MailAccountId,
        int NewMessageCount,
        MailNotificationPreview? Preview,
        bool ShowPreview);
    private sealed record ActiveMailNotification(Guid NotificationId, Guid MailAccountId);
}
