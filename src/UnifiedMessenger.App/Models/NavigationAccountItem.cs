using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedMessenger.App.Models;

public sealed class NavigationAccountItem : ObservableObject, IDisposable
{
    private readonly ServiceInstance? _service;
    private readonly MailAccount? _mailAccount;
    private bool _disposed;

    private NavigationAccountItem(ServiceInstance service)
    {
        _service = service;
        service.PropertyChanged += OnUnderlyingPropertyChanged;
    }

    private NavigationAccountItem(MailAccount mailAccount)
    {
        _mailAccount = mailAccount;
        mailAccount.PropertyChanged += OnUnderlyingPropertyChanged;
    }

    public Guid Id => _service?.Id ?? _mailAccount!.Id;
    public ServiceInstance? Service => _service;
    public MailAccount? MailAccount => _mailAccount;
    public bool IsWebService => _service is not null;
    public bool IsMailAccount => _mailAccount is not null;
    public bool IsEnabled => _service?.IsEnabled ?? _mailAccount!.IsEnabled;
    public string DisplayName => _service?.DisplayName ?? _mailAccount!.DisplayLabel;
    public string Glyph => _service is not null
        ? _service.ServiceType switch
        {
            ServiceType.Telegram => "T",
            ServiceType.WhatsApp => "W",
            ServiceType.Max => "M",
            ServiceType.VkMessenger => "VK",
            _ => "?"
        }
        : _mailAccount!.Provider switch
        {
            MailProviderType.Gmail => "G",
            MailProviderType.Yandex => "Y",
            MailProviderType.MailRu => "@",
            MailProviderType.GenericImap => "M",
            _ => "M"
        };
    public int SidebarBadgeCount => _service?.SidebarBadgeCount ?? _mailAccount?.InboxUnreadCount ?? 0;
    public string? UnreadBadgeText => SidebarBadgeFormatter.Format(SidebarBadgeCount);
    public bool ShowUnreadBadge => IsEnabled && SidebarBadgeCount > 0;

    public static NavigationAccountItem FromService(ServiceInstance service) =>
        new(service ?? throw new ArgumentNullException(nameof(service)));

    public static NavigationAccountItem FromMail(MailAccount mailAccount) =>
        new(mailAccount ?? throw new ArgumentNullException(nameof(mailAccount)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_service is not null)
        {
            _service.PropertyChanged -= OnUnderlyingPropertyChanged;
        }

        if (_mailAccount is not null)
        {
            _mailAccount.PropertyChanged -= OnUnderlyingPropertyChanged;
        }
    }

    private void OnUnderlyingPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ServiceInstance.DisplayName)
            or nameof(MailAccount.DisplayName)
            or nameof(MailAccount.EmailAddress)
            or nameof(MailAccount.DisplayLabel))
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        if (eventArgs.PropertyName is nameof(ServiceInstance.IsEnabled) or nameof(MailAccount.IsEnabled))
        {
            OnPropertyChanged(nameof(IsEnabled));
        }

        if (_service is not null
            && eventArgs.PropertyName is (
                nameof(ServiceInstance.UnreadCount)
                or nameof(ServiceInstance.LanternUnviewedActivityCount)
                or nameof(ServiceInstance.SidebarBadgeCount)
                or nameof(ServiceInstance.UnreadBadgeText)
                or nameof(ServiceInstance.HasUnreadActivity)
                or nameof(ServiceInstance.ShowUnreadBadge)))
        {
            OnPropertyChanged(nameof(SidebarBadgeCount));
            OnPropertyChanged(nameof(UnreadBadgeText));
            OnPropertyChanged(nameof(ShowUnreadBadge));
        }

        if (_mailAccount is not null
            && eventArgs.PropertyName is (
                nameof(MailAccount.InboxUnreadCount)
                or nameof(MailAccount.UnreadBadgeText)
                or nameof(MailAccount.ShowUnreadBadge)))
        {
            OnPropertyChanged(nameof(SidebarBadgeCount));
            OnPropertyChanged(nameof(UnreadBadgeText));
            OnPropertyChanged(nameof(ShowUnreadBadge));
        }
    }
}
