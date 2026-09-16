using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;

namespace UnifiedMessenger.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IBuiltInServiceCatalog _serviceCatalog;
    private readonly INotificationSoundFilePicker? _notificationSoundFilePicker;
    private bool _disposed;

    public SettingsViewModel(
        MainWindowViewModel mainWindowViewModel,
        IBuiltInServiceCatalog serviceCatalog,
        INotificationSoundFilePicker? notificationSoundFilePicker = null)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _serviceCatalog = serviceCatalog;
        _notificationSoundFilePicker = notificationSoundFilePicker;
        Sections =
        [
            new(SettingsSection.General, "Общие", "⚙"),
            new(SettingsSection.Notifications, "Уведомления", "●"),
            new(SettingsSection.Accounts, "Аккаунты", "☰"),
            new(SettingsSection.About, "О программе", "i")
        ];
        _selectedSection = Sections[0];
        SynchronizeAccounts();
        _mainWindowViewModel.Services.CollectionChanged += OnServicesCollectionChanged;
        _mainWindowViewModel.MailAccounts.CollectionChanged += OnMailAccountsCollectionChanged;
        _mainWindowViewModel.PropertyChanged += OnMainWindowPropertyChanged;
    }

    public event EventHandler<SettingsAccountEventArgs>? RenameAccountRequested;
    public event EventHandler<SettingsAccountEnabledEventArgs>? AccountEnabledChangeRequested;
    public event EventHandler<SettingsAccountEventArgs>? DeleteAccountRequested;
    public event EventHandler? AddMailAccountRequested;
    public event EventHandler<SettingsMailAccountEventArgs>? RenameMailAccountRequested;
    public event EventHandler<SettingsMailAccountEventArgs>? ChangeMailAccountPasswordRequested;
    public event EventHandler<SettingsMailAccountEnabledEventArgs>? MailAccountEnabledChangeRequested;
    public event EventHandler<SettingsMailAccountEventArgs>? DeleteMailAccountRequested;

    public IReadOnlyList<SettingsSectionItem> Sections { get; }
    public ObservableCollection<SettingsAccountViewModel> Accounts { get; } = [];
    public ObservableCollection<SettingsMailAccountViewModel> MailAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAccountsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAboutSelected))]
    private SettingsSectionItem _selectedSection;

    public bool IsGeneralSelected => SelectedSection.Section is SettingsSection.General;
    public bool IsNotificationsSelected => SelectedSection.Section is SettingsSection.Notifications;
    public bool IsAccountsSelected => SelectedSection.Section is SettingsSection.Accounts;
    public bool IsAboutSelected => SelectedSection.Section is SettingsSection.About;

    public bool CloseToTray => _mainWindowViewModel.CloseToTray;
    public bool HasShownTrayHint => _mainWindowViewModel.HasShownTrayHint;
    public bool NotificationsEnabled => _mainWindowViewModel.NotificationsEnabled;
    public bool DoNotDisturb => _mainWindowViewModel.DoNotDisturb;
    public bool ShowNotificationPreview => _mainWindowViewModel.ShowNotificationPreview;
    public bool NotificationSoundEnabled => _mainWindowViewModel.NotificationSoundEnabled;
    public bool AutomaticallyShowRemoteImages => _mainWindowViewModel.AutomaticallyShowRemoteImages;
    public bool IsTelegramLanternSound =>
        _mainWindowViewModel.TelegramNotificationSoundMode is NotificationSoundMode.Lantern;
    public bool IsTelegramNativeSound => !IsTelegramLanternSound;
    public bool IsWhatsAppLanternSound =>
        _mainWindowViewModel.WhatsAppNotificationSoundMode is NotificationSoundMode.Lantern;
    public bool IsWhatsAppNativeSound => !IsWhatsAppLanternSound;
    public bool IsMaxLanternSound =>
        _mainWindowViewModel.MaxNotificationSoundMode is NotificationSoundMode.Lantern;
    public bool IsMaxNativeSound => !IsMaxLanternSound;
    public bool IsDefaultLanternSound =>
        _mainWindowViewModel.LanternSoundSource is LanternSoundSource.Default;
    public bool IsCustomLanternSound => !IsDefaultLanternSound;
    public string LanternCustomSoundDisplayName =>
        _mainWindowViewModel.LanternCustomSoundDisplayName ?? "Файл не выбран";
    public string ApplicationVersion => CreateApplicationVersion();

    [ObservableProperty]
    private string? _soundStatusMessage;

    [RelayCommand]
    private void Close() => _mainWindowViewModel.CloseSettingsCommand.Execute(null);

    public void RequestAddMailAccount() => AddMailAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SetCloseToTray(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetCloseToTrayAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task ResetTrayHint()
    {
        await _mainWindowViewModel.ResetTrayHintAsync();
    }

    [RelayCommand]
    private async Task SetNotificationsEnabled(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetNotificationsEnabledAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task SetDoNotDisturb(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetDoNotDisturbAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task SetShowNotificationPreview(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetShowNotificationPreviewAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task SetNotificationSoundEnabled(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetNotificationSoundEnabledAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task SetAutomaticallyShowRemoteImages(bool? value)
    {
        if (value is bool enabled)
        {
            await _mainWindowViewModel.SetAutomaticallyShowRemoteImagesAsync(enabled);
        }
    }

    [RelayCommand]
    private async Task SetTelegramSoundMode(NotificationSoundMode? mode) =>
        await SetServiceSoundModeAsync(ServiceType.Telegram, mode);

    [RelayCommand]
    private async Task SetWhatsAppSoundMode(NotificationSoundMode? mode) =>
        await SetServiceSoundModeAsync(ServiceType.WhatsApp, mode);

    [RelayCommand]
    private async Task SetMaxSoundMode(NotificationSoundMode? mode) =>
        await SetServiceSoundModeAsync(ServiceType.Max, mode);

    [RelayCommand]
    private async Task SelectDefaultLanternSound()
    {
        await _mainWindowViewModel.RestoreDefaultLanternSoundAsync();
        SoundStatusMessage = "Используется стандартный звук Lantern.";
    }

    [RelayCommand]
    private async Task SelectCustomLanternSound()
    {
        if (await _mainWindowViewModel.UseExistingCustomLanternSoundAsync())
        {
            SoundStatusMessage = "Используется выбранный пользовательский звук.";
            return;
        }

        await ChooseLanternSound();
    }

    [RelayCommand]
    private async Task ChooseLanternSound()
    {
        string? selectedPath = _notificationSoundFilePicker?.SelectSoundFile();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            RefreshLanternSoundBindings();
            return;
        }

        LanternSoundImportResult result = await _mainWindowViewModel.ImportCustomLanternSoundAsync(
            selectedPath);
        SoundStatusMessage = result.Success
            ? "Пользовательский звук сохранён внутри Lantern."
            : result.ErrorMessage;
        RefreshLanternSoundBindings();
    }

    [RelayCommand]
    private void PreviewLanternSound()
    {
        SoundStatusMessage = _mainWindowViewModel.PreviewLanternSound()
            ? "Воспроизводится текущий звук Lantern."
            : "Не удалось воспроизвести звук; будет использован безопасный fallback.";
    }

    [RelayCommand]
    private async Task RestoreDefaultLanternSound()
    {
        await _mainWindowViewModel.RestoreDefaultLanternSoundAsync();
        SoundStatusMessage = "Стандартный звук Lantern восстановлен.";
    }

    internal void OpenAccount(ServiceInstance service) => _mainWindowViewModel.SelectService(service.Id);

    internal void RequestRename(ServiceInstance service) =>
        RenameAccountRequested?.Invoke(this, new SettingsAccountEventArgs(service));

    internal void RequestSetEnabled(ServiceInstance service, bool isEnabled) =>
        AccountEnabledChangeRequested?.Invoke(this, new SettingsAccountEnabledEventArgs(service, isEnabled));

    internal void RequestDelete(ServiceInstance service) =>
        DeleteAccountRequested?.Invoke(this, new SettingsAccountEventArgs(service));

    internal async Task SetMutedAsync(ServiceInstance service, bool isMuted) =>
        await _mainWindowViewModel.SetServiceMutedAsync(service, isMuted);

    internal async Task MoveAsync(ServiceInstance service, int offset)
    {
        await _mainWindowViewModel.MoveServiceAsync(service, offset);
        NotifyAccountOrderChanged();
    }

    internal void OpenMailAccount(MailAccount account) => _mainWindowViewModel.SelectMailAccount(account.Id);

    internal void RequestRenameMail(MailAccount account) =>
        RenameMailAccountRequested?.Invoke(this, new SettingsMailAccountEventArgs(account));

    internal void RequestChangeMailPassword(MailAccount account) =>
        ChangeMailAccountPasswordRequested?.Invoke(this, new SettingsMailAccountEventArgs(account));

    internal void RequestSetMailEnabled(MailAccount account, bool isEnabled) =>
        MailAccountEnabledChangeRequested?.Invoke(
            this,
            new SettingsMailAccountEnabledEventArgs(account, isEnabled));

    internal void RequestDeleteMail(MailAccount account) =>
        DeleteMailAccountRequested?.Invoke(this, new SettingsMailAccountEventArgs(account));

    internal bool CanMove(ServiceInstance service, int offset)
    {
        int index = _mainWindowViewModel.Services.IndexOf(service);
        int target = index + offset;
        return index >= 0 && target >= 0 && target < _mainWindowViewModel.Services.Count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mainWindowViewModel.Services.CollectionChanged -= OnServicesCollectionChanged;
        _mainWindowViewModel.MailAccounts.CollectionChanged -= OnMailAccountsCollectionChanged;
        _mainWindowViewModel.PropertyChanged -= OnMainWindowPropertyChanged;
        foreach (SettingsAccountViewModel account in Accounts)
        {
            account.Dispose();
        }

        Accounts.Clear();
        foreach (SettingsMailAccountViewModel account in MailAccounts)
        {
            account.Dispose();
        }

        MailAccounts.Clear();
    }

    private void OnServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        SynchronizeAccounts();

    private void OnMailAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        SynchronizeMailAccounts();

    private void OnMainWindowPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.CloseToTray))
        {
            OnPropertyChanged(nameof(CloseToTray));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.HasShownTrayHint))
        {
            OnPropertyChanged(nameof(HasShownTrayHint));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.NotificationsEnabled))
        {
            OnPropertyChanged(nameof(NotificationsEnabled));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.DoNotDisturb))
        {
            OnPropertyChanged(nameof(DoNotDisturb));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.ShowNotificationPreview))
        {
            OnPropertyChanged(nameof(ShowNotificationPreview));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.NotificationSoundEnabled))
        {
            OnPropertyChanged(nameof(NotificationSoundEnabled));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.AutomaticallyShowRemoteImages))
        {
            OnPropertyChanged(nameof(AutomaticallyShowRemoteImages));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.TelegramNotificationSoundMode))
        {
            OnPropertyChanged(nameof(IsTelegramLanternSound));
            OnPropertyChanged(nameof(IsTelegramNativeSound));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.WhatsAppNotificationSoundMode))
        {
            OnPropertyChanged(nameof(IsWhatsAppLanternSound));
            OnPropertyChanged(nameof(IsWhatsAppNativeSound));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.MaxNotificationSoundMode))
        {
            OnPropertyChanged(nameof(IsMaxLanternSound));
            OnPropertyChanged(nameof(IsMaxNativeSound));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.LanternSoundSource)
                 or nameof(MainWindowViewModel.LanternCustomSoundDisplayName))
        {
            RefreshLanternSoundBindings();
        }
    }

    private void SynchronizeAccounts()
    {
        foreach (SettingsAccountViewModel account in Accounts)
        {
            account.Dispose();
        }

        Accounts.Clear();
        foreach (ServiceInstance service in _mainWindowViewModel.Services)
        {
            Accounts.Add(
                new SettingsAccountViewModel(
                    this,
                    service,
                    _serviceCatalog.Get(service.ServiceType).DisplayName));
        }

        NotifyAccountOrderChanged();
        SynchronizeMailAccounts();
    }

    private void SynchronizeMailAccounts()
    {
        foreach (SettingsMailAccountViewModel account in MailAccounts)
        {
            account.Dispose();
        }

        MailAccounts.Clear();
        foreach (MailAccount account in _mainWindowViewModel.MailAccounts.OrderBy(account => account.SortOrder))
        {
            MailAccounts.Add(new SettingsMailAccountViewModel(this, account));
        }
    }

    private void NotifyAccountOrderChanged()
    {
        foreach (SettingsAccountViewModel account in Accounts)
        {
            account.NotifyOrderChanged();
        }
    }

    private static string CreateApplicationVersion()
    {
        Version? version = typeof(SettingsViewModel).Assembly.GetName().Version;
        return version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private async Task SetServiceSoundModeAsync(
        ServiceType serviceType,
        NotificationSoundMode? mode)
    {
        if (mode is NotificationSoundMode selectedMode)
        {
            await _mainWindowViewModel.SetNotificationSoundModeAsync(serviceType, selectedMode);
        }
    }

    private void RefreshLanternSoundBindings()
    {
        OnPropertyChanged(nameof(IsDefaultLanternSound));
        OnPropertyChanged(nameof(IsCustomLanternSound));
        OnPropertyChanged(nameof(LanternCustomSoundDisplayName));
    }
}
