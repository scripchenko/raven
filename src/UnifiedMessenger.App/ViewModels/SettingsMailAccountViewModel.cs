using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.ViewModels;

public sealed partial class SettingsMailAccountViewModel : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _owner;
    private bool _disposed;

    internal SettingsMailAccountViewModel(SettingsViewModel owner, MailAccount account)
    {
        _owner = owner;
        Account = account;
        Account.PropertyChanged += OnAccountPropertyChanged;
    }

    public MailAccount Account { get; }
    public string DisplayName => Account.DisplayLabel;
    public string EmailAddress => Account.EmailAddress;
    public string ProviderLabel => Account.Provider switch
    {
        MailProviderType.Gmail => "Gmail",
        MailProviderType.Yandex => "Yandex Mail",
        MailProviderType.MailRu => "Mail.ru",
        MailProviderType.GenericImap => "IMAP/SMTP",
        _ => UnifiedMessenger.App.Services.Localization.Localizer.Instance.Get("Mail")
    };
    public string Glyph => Account.Provider switch
    {
        MailProviderType.Gmail => "G",
        MailProviderType.Yandex => "Y",
        MailProviderType.MailRu => "@",
        _ => "M"
    };
    public bool IsEnabled => Account.IsEnabled;
    public bool CanChangeAppPassword =>
        MailProviderFeaturePolicies.Get(Account.Provider).SupportsAppPasswordReplacement;

    [RelayCommand]
    private void Open() => _owner.OpenMailAccount(Account);

    [RelayCommand]
    private void Rename() => _owner.RequestRenameMail(Account);

    [RelayCommand]
    private void ChangeAppPassword() => _owner.RequestChangeMailPassword(Account);

    [RelayCommand]
    private void SetEnabled(bool? value)
    {
        if (value is bool isEnabled && isEnabled != Account.IsEnabled)
        {
            _owner.RequestSetMailEnabled(Account, isEnabled);
        }
    }

    [RelayCommand]
    private void Delete() => _owner.RequestDeleteMail(Account);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Account.PropertyChanged -= OnAccountPropertyChanged;
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MailAccount.DisplayName) or nameof(MailAccount.EmailAddress))
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(EmailAddress));
        }
        else if (eventArgs.PropertyName is nameof(MailAccount.IsEnabled))
        {
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
