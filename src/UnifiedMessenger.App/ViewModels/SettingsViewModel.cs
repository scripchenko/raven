using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Updates;
using UnifiedMessenger.App.Services.Localization;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IBuiltInServiceCatalog _serviceCatalog;
    private readonly INotificationSoundFilePicker? _notificationSoundFilePicker;
    private readonly IUpdateCheckService? _updateCheckService;
    private readonly IExternalBrowserService? _externalBrowserService;
    private UpdateCheckResult? _lastUpdateCheckResult;
    private bool _disposed;

    public SettingsViewModel(
        MainWindowViewModel mainWindowViewModel,
        IBuiltInServiceCatalog serviceCatalog,
        INotificationSoundFilePicker? notificationSoundFilePicker = null,
        IUpdateCheckService? updateCheckService = null,
        IExternalBrowserService? externalBrowserService = null)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _serviceCatalog = serviceCatalog;
        _notificationSoundFilePicker = notificationSoundFilePicker;
        _updateCheckService = updateCheckService;
        _externalBrowserService = externalBrowserService;
        Sections = [];
        RebuildSections(SettingsSection.General);
        _selectedSection = Sections[0];
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
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

    public ObservableCollection<SettingsSectionItem> Sections { get; }
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
    public string Language => _mainWindowViewModel.Language;
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
        _mainWindowViewModel.LanternCustomSoundDisplayName ?? Localizer.Instance.Get("No file selected");
    public string ApplicationVersion => $"raven {(_updateCheckService?.CurrentVersion ?? new Version(0, 1, 0)).ToString(3)}";
    public string ApplicationVersionDisplay => Localizer.Instance.Format("Version {0}", ApplicationVersion);
    public string? UpdateStatusMessage { get; private set; }
    public bool HasUpdateAvailable { get; private set; }
    public string SupportLabel => Localizer.Instance.Get("Support: @dscripchenko");
    public Uri SupportUri => new("https://t.me/dscripchenko");

    [ObservableProperty]
    private string? _soundStatusMessage;

    [RelayCommand]
    private void Close() => _mainWindowViewModel.CloseSettingsCommand.Execute(null);

    [RelayCommand]
    private Task SetLanguage(string? value) => value is "en" or "ru"
        ? _mainWindowViewModel.SetLanguageAsync(value)
        : Task.CompletedTask;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (_updateCheckService is null)
        {
            UpdateStatusMessage = Localizer.Instance.Get("Unable to check for updates.");
            OnPropertyChanged(nameof(UpdateStatusMessage));
            return;
        }

        UpdateCheckResult result = await _updateCheckService.CheckAsync(manual: true);
        _lastUpdateCheckResult = result;
        HasUpdateAvailable = result.Status is UpdateCheckStatus.UpdateAvailable;
        UpdateStatusMessage = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable when result.Release is not null
                => Localizer.Instance.Format("A new version of raven {0} is available.", result.Release.Version),
            UpdateCheckStatus.Current => Localizer.Instance.Get("You are using the latest version of raven."),
            UpdateCheckStatus.NoRelease => Localizer.Instance.Get("No public release is available yet."),
            _ => Localizer.Instance.Get("Unable to check for updates.")
        };
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(UpdateStatusMessage));
        if (result.Release is not null)
        {
            _pendingReleaseUrl = result.Release.ReleaseUrl;
        }
        OnPropertyChanged(nameof(CanDownloadUpdate));
        DownloadUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private void DownloadUpdate()
    {
        if (_pendingReleaseUrl is not null)
        {
            _externalBrowserService?.TryOpen(_pendingReleaseUrl);
        }
    }

    [RelayCommand]
    private void OpenSupport() => _externalBrowserService?.TryOpen(SupportUri);

    public bool CanDownloadUpdate => HasUpdateAvailable && _pendingReleaseUrl is not null;
    private Uri? _pendingReleaseUrl;

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
        SoundStatusMessage = Localizer.Instance.Get("Using the default raven sound.");
    }

    [RelayCommand]
    private async Task SelectCustomLanternSound()
    {
        if (await _mainWindowViewModel.UseExistingCustomLanternSoundAsync())
        {
            SoundStatusMessage = Localizer.Instance.Get("Using the selected custom sound.");
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
            ? Localizer.Instance.Get("The custom sound is saved inside raven.")
            : result.ErrorMessage;
        RefreshLanternSoundBindings();
    }

    [RelayCommand]
    private void PreviewLanternSound()
    {
        SoundStatusMessage = _mainWindowViewModel.PreviewLanternSound()
            ? Localizer.Instance.Get("Playing the current raven sound.")
            : Localizer.Instance.Get("Could not play the sound; a safe fallback will be used.");
    }

    [RelayCommand]
    private async Task RestoreDefaultLanternSound()
    {
        await _mainWindowViewModel.RestoreDefaultLanternSoundAsync();
        SoundStatusMessage = Localizer.Instance.Get("The default raven sound has been restored.");
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
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;
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
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.Language))
        {
            OnPropertyChanged(nameof(Language));
        }
        else if (eventArgs.PropertyName is nameof(MainWindowViewModel.CloseToTray))
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

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != "Item[]")
        {
            return;
        }

        RebuildSections(SelectedSection.Section);
        OnPropertyChanged(nameof(SupportLabel));
        OnPropertyChanged(nameof(ApplicationVersionDisplay));
        OnPropertyChanged(nameof(LanternCustomSoundDisplayName));
        if (_lastUpdateCheckResult is UpdateCheckResult result)
        {
            UpdateStatusMessage = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable when result.Release is not null
                    => Localizer.Instance.Format("A new version of raven {0} is available.", result.Release.Version),
                UpdateCheckStatus.Current => Localizer.Instance.Get("You are using the latest version of raven."),
                UpdateCheckStatus.NoRelease => Localizer.Instance.Get("No public release is available yet."),
                _ => Localizer.Instance.Get("Unable to check for updates.")
            };
            OnPropertyChanged(nameof(UpdateStatusMessage));
        }
    }

    private void RebuildSections(SettingsSection selected)
    {
        Sections.Clear();
        Sections.Add(new(SettingsSection.General, Localizer.Instance.Get("General"), "⚙"));
        Sections.Add(new(SettingsSection.Notifications, Localizer.Instance.Get("Notifications"), "●"));
        Sections.Add(new(SettingsSection.Accounts, Localizer.Instance.Get("Accounts"), "☰"));
        Sections.Add(new(SettingsSection.About, Localizer.Instance.Get("About"), "i"));
        if (Sections.FirstOrDefault(section => section.Section == selected) is { } item)
        {
            SelectedSection = item;
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
