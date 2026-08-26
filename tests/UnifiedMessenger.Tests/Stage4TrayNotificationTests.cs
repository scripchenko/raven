using System.Drawing;
using System.IO;
using System.Text.Json;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage4TrayNotificationTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Fact]
    public async Task Migration_FromSchema2PreservesAccountsAndAddsSafeDefaults()
    {
        using TempSettingsFolder temp = new();
        Guid telegramId = Guid.NewGuid();
        Guid vkId = Guid.NewGuid();
        string telegramProfile = ProfileNameFactory.Create(telegramId);
        string vkProfile = ProfileNameFactory.Create(vkId);
        string json = $$"""
            {
              "schemaVersion": 2,
              "lastServiceId": "{{vkId}}",
              "services": [
                {
                  "id": "{{telegramId}}",
                  "serviceType": "Telegram",
                  "displayName": "Telegram",
                  "profileName": "{{telegramProfile}}",
                  "isEnabled": false,
                  "sortOrder": 0,
                  "unreadCount": 17,
                  "hasUnreadActivity": true
                },
                {
                  "id": "{{vkId}}",
                  "serviceType": "VkMessenger",
                  "displayName": "VK",
                  "profileName": "{{vkProfile}}",
                  "isEnabled": true,
                  "sortOrder": 1
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(temp.SettingsPath, json);

        SettingsLoadResult result = await new JsonSettingsService(temp.SettingsPath).LoadAsync();

        Assert.True(result.WasMigrated);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(vkId, result.Settings.LastServiceId);
        Assert.Equal([telegramId, vkId], result.Settings.Services.Select(service => service.Id));
        Assert.Equal([telegramProfile, vkProfile], result.Settings.Services.Select(service => service.ProfileName));
        Assert.Equal([0, 1], result.Settings.Services.Select(service => service.SortOrder));
        Assert.False(result.Settings.Services[0].IsEnabled);
        Assert.True(result.Settings.Services[1].IsEnabled);
        Assert.True(result.Settings.CloseToTray);
        Assert.False(result.Settings.HasShownTrayHint);
        Assert.False(result.Settings.Notifications.DoNotDisturb);
        Assert.True(result.Settings.Notifications.IsEnabled);
        Assert.True(result.Settings.Notifications.ShowNotificationPreview);
        Assert.All(result.Settings.Services, service => Assert.False(service.IsMuted));
        Assert.All(result.Settings.Services, service => Assert.Equal(NotificationPermissionState.Unknown, service.NotificationPermissionState));
        Assert.All(result.Settings.Services, service => Assert.Null(service.UnreadCount));
        Assert.All(result.Settings.Services, service => Assert.False(service.HasUnreadActivity));
    }

    [Fact]
    public void Defaults_CloseToTrayAndNotificationsAreEnabled()
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.True(settings.CloseToTray);
        Assert.False(settings.HasShownTrayHint);
        Assert.False(settings.Notifications.DoNotDisturb);
        Assert.True(settings.Notifications.IsEnabled);
        Assert.True(settings.Notifications.ShowNotificationPreview);
    }

    [Fact]
    public void NotificationPermissionDecision_IsSavedInStableWebViewProfile()
    {
        Assert.True(WebViewSessionManager.SaveNotificationPermissionsInProfile);
    }

    [Fact]
    public void RuntimeActivity_IsNotSerialized()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        service.UnreadCount = 7;
        service.HasUnreadActivity = true;
        AppSettings settings = new() { Services = [service] };

        string json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("UnreadCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HasUnreadActivity", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavingSettings_DoesNotClearRuntimeActivityInMemory()
    {
        using TempSettingsFolder temp = new();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        service.UnreadCount = 7;
        service.HasUnreadActivity = true;
        AppSettings settings = new() { Services = [service] };
        JsonSettingsService persistence = new(temp.SettingsPath);

        await persistence.SaveAsync(settings);

        Assert.Equal(7, service.UnreadCount);
        Assert.True(service.HasUnreadActivity);
        string json = await File.ReadAllTextAsync(temp.SettingsPath);
        Assert.DoesNotContain("UnreadCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HasUnreadActivity", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowClose_DefaultActionIsHideToTray()
    {
        ApplicationExitCoordinator coordinator = new();

        Assert.True(coordinator.ShouldHideToTray(closeToTray: true, applicationShutdownStarted: false));
    }

    [Fact]
    public void ExplicitExit_IsNotInterceptedByCloseToTray()
    {
        ApplicationExitCoordinator coordinator = new();
        int requests = 0;
        coordinator.ExitRequested += (_, _) => requests++;

        coordinator.RequestExit();
        coordinator.RequestExit();

        Assert.True(coordinator.IsExplicitExitRequested);
        Assert.Equal(1, requests);
        Assert.False(coordinator.ShouldHideToTray(closeToTray: true, applicationShutdownStarted: false));
    }

    [Fact]
    public void Closing_WhenExitIsInProgress_IsNeverConvertedToHideToTray()
    {
        ApplicationExitCoordinator coordinator = new();
        coordinator.RequestExit();
        Assert.True(coordinator.TryBeginShutdown());

        bool shouldHide = coordinator.ShouldHideToTray(
            closeToTray: true,
            applicationShutdownStarted: false);

        Assert.True(coordinator.IsExiting);
        Assert.Equal(ApplicationShutdownState.ShuttingDown, coordinator.ShutdownState);
        Assert.False(shouldHide);
    }

    [Fact]
    public void ShutdownCoordinator_IsIdempotentAndCannotRunTwice()
    {
        ApplicationExitCoordinator coordinator = new();
        int requests = 0;
        coordinator.ExitRequested += (_, _) => requests++;

        coordinator.RequestExit();
        coordinator.RequestExit();
        bool firstBegin = coordinator.TryBeginShutdown();
        bool secondBegin = coordinator.TryBeginShutdown();
        coordinator.CompleteShutdown();
        coordinator.CompleteShutdown();

        Assert.Equal(1, requests);
        Assert.True(firstBegin);
        Assert.False(secondBegin);
        Assert.Equal(ApplicationShutdownState.Completed, coordinator.ShutdownState);
    }

    [Fact]
    public void WindowsSessionEnding_IsNotInterceptedByCloseToTray()
    {
        ApplicationExitCoordinator coordinator = new();

        coordinator.BeginSessionEnding();

        Assert.True(coordinator.IsSessionEnding);
        Assert.False(coordinator.ShouldHideToTray(closeToTray: true, applicationShutdownStarted: false));
    }

    [Fact]
    public void ApplicationShutdown_IsNotInterceptedByCloseToTray()
    {
        ApplicationExitCoordinator coordinator = new();

        Assert.False(coordinator.ShouldHideToTray(closeToTray: true, applicationShutdownStarted: true));
    }

    [Fact]
    public async Task TrayHint_IsMarkedOnlyOnceAndSaved()
    {
        AppSettings settings = AppSettings.CreateDefault();
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);

        bool first = await viewModel.MarkTrayHintShownAsync();
        bool second = await viewModel.MarkTrayHintShownAsync();

        Assert.True(first);
        Assert.False(second);
        Assert.True(settings.HasShownTrayHint);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void TrayMenu_TogglesDndSynchronizesCheckAndRequestsExit()
    {
        AppSettings settings = AppSettings.CreateDefault();
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTrayIconService tray = new();
        FakeWindowActivationService window = new();
        ApplicationExitCoordinator exit = new();
        FakeWebNotificationCoordinator notifications = new();
        using ApplicationTrayCoordinator coordinator = new(
            tray,
            window,
            exit,
            notifications,
            activity,
            store,
            viewModel,
            new ImmediateDispatcher(),
            new FakeTaskbarActivityIndicator());
        coordinator.Initialize();

        tray.RaiseDoNotDisturbToggle();
        tray.RaiseOpen();
        tray.RaiseExit();

        Assert.True(settings.Notifications.DoNotDisturb);
        Assert.True(tray.DoNotDisturb);
        Assert.Equal([true], notifications.DoNotDisturbChanges);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, window.ActivationCount);
        Assert.True(exit.IsExplicitExitRequested);
        Assert.True(notifications.WasShutdown);
    }

    [Fact]
    public void RepeatedTrayExit_DisposesTrayAndStopsNotificationsOnlyOnce()
    {
        AppSettings settings = AppSettings.CreateDefault();
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTrayIconService tray = new();
        ApplicationExitCoordinator exit = new();
        FakeWebNotificationCoordinator notifications = new();
        using ApplicationTrayCoordinator coordinator = new(
            tray,
            new FakeWindowActivationService(),
            exit,
            notifications,
            activity,
            store,
            viewModel,
            new ImmediateDispatcher(),
            new FakeTaskbarActivityIndicator());
        coordinator.Initialize();

        tray.RaiseExit();
        tray.RaiseExit();
        coordinator.BeginShutdown();

        Assert.Equal(1, tray.ShutdownCount);
        Assert.Equal(1, notifications.ShutdownCount);
        Assert.Equal(ApplicationShutdownState.ExitRequested, exit.ShutdownState);
    }

    [Fact]
    public void CloseToTrayHint_IsNotReportedAsShownWhenDndSuppressesIt()
    {
        AppSettings settings = AppSettings.CreateDefault();
        settings.Notifications.DoNotDisturb = true;
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTrayIconService tray = new();
        using ApplicationTrayCoordinator coordinator = new(
            tray,
            new FakeWindowActivationService(),
            new ApplicationExitCoordinator(),
            new FakeWebNotificationCoordinator(),
            activity,
            store,
            viewModel,
            new ImmediateDispatcher(),
            new FakeTaskbarActivityIndicator());
        coordinator.Initialize();

        bool shown = coordinator.TryShowCloseToTrayHint();

        Assert.False(shown);
        Assert.Equal(0, tray.BalloonCount);
        Assert.False(settings.HasShownTrayHint);
    }

    [Fact]
    public async Task DoNotDisturb_TogglesAndPersists()
    {
        AppSettings settings = AppSettings.CreateDefault();
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);

        await viewModel.ToggleDoNotDisturbCommand.ExecuteAsync(null);

        Assert.True(settings.Notifications.DoNotDisturb);
        Assert.True(viewModel.DoNotDisturb);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task SelectedAccountMute_TogglesAndPersists()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        settings.Services.Add(service);
        FakeSettingsStore store = new(settings);
        FakeWebNotificationCoordinator notifications = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, notificationCoordinator: notifications);

        await viewModel.ToggleSelectedMuteCommand.ExecuteAsync(null);

        Assert.True(service.IsMuted);
        Assert.True(viewModel.IsSelectedServiceMuted);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(service.Id, Assert.Single(notifications.DiscardedServiceIds));
    }

    [Theory]
    [InlineData("(3) Telegram", 3)]
    [InlineData("(99) WhatsApp", 99)]
    [InlineData("(0) MAX", 0)]
    [InlineData("Telegram 3", null)]
    [InlineData("Telegram (3)", null)]
    [InlineData("(-1) Telegram", null)]
    [InlineData("(10000) Telegram", null)]
    public void PageTitleParser_UsesOnlySafeLeadingCount(string title, int? expected)
    {
        Assert.Equal(expected, PageTitleUnreadCountParser.TryParse(title));
    }

    [Fact]
    public void ActivityCoordinator_UpdatesKnownCountAndFormats99Plus()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance service = CreateService(ServiceType.WhatsApp);

        coordinator.UpdateFromDocumentTitle(service, "(104) WhatsApp", markActivity: true);

        Assert.Equal(104, service.UnreadCount);
        Assert.True(service.HasUnreadActivity);
        Assert.Equal("99+", service.UnreadBadgeText);
        Assert.True(service.ShowUnreadBadge);
    }

    [Fact]
    public void NotificationActivity_UsesSeparateLocalUnviewedEventCount()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance service = CreateService(ServiceType.Max);

        coordinator.MarkNotificationReceived(service);

        Assert.True(service.HasUnreadActivity);
        Assert.Null(service.UnreadCount);
        Assert.Equal(1, service.LanternUnviewedActivityCount);
        Assert.Equal("1", service.UnreadBadgeText);
        Assert.True(service.ShowUnreadBadge);
    }

    [Fact]
    public void SwitchingAwayFromAccount_DoesNotClearItsActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance first = CreateService(ServiceType.Telegram);
        ServiceInstance second = CreateService(ServiceType.VkMessenger);
        settings.Services.AddRange([first, second]);
        settings.LastServiceId = first.Id;
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        first.HasUnreadActivity = true;

        viewModel.SelectService(second.Id);

        Assert.True(first.HasUnreadActivity);
    }

    [Fact]
    public void TelegramAndWhatsAppActivity_AreClearedOnlyWhenTheirOwnAccountIsViewed()
    {
        ServiceInstance telegram = CreateService(ServiceType.Telegram);
        ServiceInstance whatsapp = CreateService(ServiceType.WhatsApp);
        ServiceInstance max = CreateService(ServiceType.Max);
        ServiceInstance vk = CreateService(ServiceType.VkMessenger);
        AppSettings settings = AppSettings.CreateDefault();
        settings.Services.AddRange([telegram, whatsapp, max, vk]);
        settings.LastServiceId = max.Id;
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        telegram.HasUnreadActivity = true;
        whatsapp.HasUnreadActivity = true;

        viewModel.SelectService(vk.Id);
        viewModel.MarkSelectedServiceViewed(true, true);
        Assert.True(telegram.HasUnreadActivity);
        Assert.True(whatsapp.HasUnreadActivity);

        viewModel.SelectService(whatsapp.Id);
        viewModel.MarkSelectedServiceViewed(true, true);
        Assert.True(telegram.HasUnreadActivity);
        Assert.False(whatsapp.HasUnreadActivity);

        viewModel.SelectService(telegram.Id);
        viewModel.MarkSelectedServiceViewed(true, true);
        Assert.False(telegram.HasUnreadActivity);
    }

    [Fact]
    public void StartupPrimeVisibility_DoesNotMarkSelectedServiceViewed()
    {
        ServiceInstance max = CreateService(ServiceType.Max);
        max.HasUnreadActivity = true;
        AppSettings settings = AppSettings.CreateDefault();
        settings.Services.Add(max);
        settings.LastServiceId = max.Id;
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        max.HasUnreadActivity = true;

        viewModel.MarkSelectedServiceViewed(
            isMainWindowVisible: false,
            isMainWindowActive: false);

        Assert.True(max.HasUnreadActivity);
    }

    [Fact]
    public void EnteringAccount_WhileWindowIsVisibleAndActive_ClearsActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance first = CreateService(ServiceType.Telegram);
        ServiceInstance second = CreateService(ServiceType.VkMessenger);
        settings.Services.AddRange([first, second]);
        settings.LastServiceId = second.Id;
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        first.HasUnreadActivity = true;
        first.UnreadCount = 4;

        viewModel.SelectService(first.Id);
        viewModel.MarkSelectedServiceViewed(isMainWindowVisible: true, isMainWindowActive: true);

        Assert.Null(first.UnreadCount);
        Assert.False(first.HasUnreadActivity);
    }

    [Fact]
    public void RestoringActiveWindow_ClearsAlreadySelectedAccountActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        settings.Services.Add(service);
        settings.LastServiceId = service.Id;
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        service.HasUnreadActivity = true;

        viewModel.MarkSelectedServiceViewed(isMainWindowVisible: true, isMainWindowActive: true);

        Assert.False(service.HasUnreadActivity);
    }

    [Fact]
    public void HiddenOrInactiveWindow_DoesNotClearSelectedAccountActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        settings.Services.Add(service);
        FakeSettingsStore store = new(settings);
        using MainWindowViewModel viewModel = CreateViewModel(settings, store);
        service.HasUnreadActivity = true;

        viewModel.MarkSelectedServiceViewed(isMainWindowVisible: false, isMainWindowActive: false);

        Assert.True(service.HasUnreadActivity);
    }

    [Fact]
    public void ZeroTitleCount_DoesNotClearUnseenNotificationActivity()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        coordinator.MarkNotificationReceived(service);

        coordinator.UpdateFromDocumentTitle(service, "(0) Telegram", markActivity: false);

        Assert.True(service.HasUnreadActivity);
        Assert.Equal(1, service.LanternUnviewedActivityCount);
        Assert.True(service.ShowUnreadBadge);
    }

    [Fact]
    public void DisabledAccount_DoesNotDisplayStaleBadge()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        service.UnreadCount = 8;
        service.HasUnreadActivity = true;

        service.IsEnabled = false;

        Assert.False(service.ShowUnreadBadge);
    }

    [Fact]
    public void TrayTooltip_SumsKnownCountsOnly()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance first = CreateService(ServiceType.Telegram);
        ServiceInstance second = CreateService(ServiceType.WhatsApp);
        first.UnreadCount = 2;
        first.HasUnreadActivity = true;
        second.UnreadCount = 3;
        second.HasUnreadActivity = true;

        string tooltip = coordinator.CreateTrayToolTip([first, second]);

        Assert.Equal("Lantern — 5 непрочитанных", tooltip);
    }

    [Fact]
    public void TrayTooltip_ReportsUnknownActivityWithoutInventingCount()
    {
        ServiceActivityCoordinator coordinator = new();
        ServiceInstance service = CreateService(ServiceType.VkMessenger);
        service.HasUnreadActivity = true;

        string tooltip = coordinator.CreateTrayToolTip([service]);

        Assert.Equal("Lantern — есть новые события", tooltip);
    }

    [Fact]
    public void TaskbarOverlay_AppearsWhenEnabledAccountHasActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        settings.Services.Add(service);
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTaskbarActivityIndicator taskbar = new();
        using ApplicationTrayCoordinator coordinator = CreateTrayCoordinator(
            store,
            activity,
            viewModel,
            taskbar);
        coordinator.Initialize();

        activity.MarkNotificationReceived(service);

        Assert.True(taskbar.HasActivity);
    }

    [Fact]
    public void TaskbarOverlay_DisappearsWhenNoEnabledAccountHasActivity()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance service = CreateService(ServiceType.Telegram);
        settings.Services.Add(service);
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTaskbarActivityIndicator taskbar = new();
        using ApplicationTrayCoordinator coordinator = CreateTrayCoordinator(
            store,
            activity,
            viewModel,
            taskbar);
        coordinator.Initialize();
        activity.MarkNotificationReceived(service);

        activity.Clear(service);

        Assert.False(taskbar.HasActivity);
    }

    [Fact]
    public void TaskbarOverlay_RemainsUntilEveryEnabledAccountActivityIsCleared()
    {
        AppSettings settings = AppSettings.CreateDefault();
        ServiceInstance telegram = CreateService(ServiceType.Telegram);
        ServiceInstance whatsapp = CreateService(ServiceType.WhatsApp);
        settings.Services.AddRange([telegram, whatsapp]);
        FakeSettingsStore store = new(settings);
        ServiceActivityCoordinator activity = new();
        using MainWindowViewModel viewModel = CreateViewModel(settings, store, activity);
        FakeTaskbarActivityIndicator taskbar = new();
        using ApplicationTrayCoordinator coordinator = CreateTrayCoordinator(
            store,
            activity,
            viewModel,
            taskbar);
        coordinator.Initialize();
        activity.MarkNotificationReceived(telegram);
        activity.MarkNotificationReceived(whatsapp);

        activity.Clear(whatsapp);

        Assert.True(taskbar.HasActivity);

        activity.Clear(telegram);

        Assert.False(taskbar.HasActivity);
    }

    [Fact]
    public void MutedAccount_SuppressesPopupButKeepsActivity()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        fixture.Service.IsMuted = true;
        LifecycleProbe lifecycle = new();

        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.Requests);
        Assert.True(fixture.Service.HasUnreadActivity);
        Assert.Equal(1, lifecycle.ShownCount);
        Assert.Equal(1, lifecycle.ClosedCount);
    }

    [Fact]
    public void Dnd_SuppressesPopupWithoutClearingBadge()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        fixture.Settings.Notifications.DoNotDisturb = true;
        LifecycleProbe lifecycle = new();

        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.Requests);
        Assert.True(fixture.Service.HasUnreadActivity);
        Assert.Null(fixture.Service.UnreadCount);
    }

    [Fact]
    public void ActiveVisibleSelectedAccount_DoesNotCreateFalseActivityOrPopup()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        fixture.Window.IsMainWindowActive = true;
        fixture.Window.IsMainWindowVisible = true;
        fixture.Window.SelectedServiceId = fixture.Service.Id;

        fixture.Coordinator.Handle(fixture.Request(new LifecycleProbe()));

        Assert.Empty(fixture.Popup.Shown);
        Assert.Empty(fixture.Sound.Requests);
        Assert.False(fixture.Service.HasUnreadActivity);
    }

    [Fact]
    public void HiddenTelegramNotification_RequestsUnifiedMessengerSound()
    {
        using NotificationFixture fixture = CreateNotificationFixture();

        fixture.Coordinator.Handle(fixture.Request(new LifecycleProbe()));

        TelegramNotificationSoundRequest request = Assert.Single(fixture.Sound.Requests);
        Assert.Equal(fixture.Service.Id, request.ServiceInstanceId);
        Assert.Equal(ServiceType.Telegram, request.ServiceType);
    }

    [Fact]
    public void ExternalNotificationOrigin_IsDenied()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe lifecycle = new();

        fixture.Coordinator.Handle(
            new WebNotificationRequest(
                fixture.Service.Id,
                "https://example.com/",
                "Private title",
                "Private body",
                lifecycle.Lifecycle));

        Assert.Empty(fixture.Popup.Shown);
        Assert.False(fixture.Service.HasUnreadActivity);
        Assert.True(lifecycle.Lifecycle.IsCompleted);
    }

    [Fact]
    public void PopupClick_ReportsOnlyClickedAndActivatesCorrectAccount()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe lifecycle = new();
        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Guid notificationId = Assert.Single(fixture.Popup.Shown).NotificationId;
        fixture.Popup.RaiseClicked(notificationId);

        Assert.Equal(1, lifecycle.ShownCount);
        Assert.Equal(1, lifecycle.ClickedCount);
        Assert.Equal(0, lifecycle.ClosedCount);
        Assert.Equal(fixture.Service.Id, Assert.Single(fixture.Window.ActivatedServiceIds));
    }

    [Fact]
    public void PopupClose_ReportsOnlyClosed()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe lifecycle = new();
        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Guid notificationId = Assert.Single(fixture.Popup.Shown).NotificationId;
        fixture.Popup.Close(notificationId);

        Assert.Equal(1, lifecycle.ShownCount);
        Assert.Equal(0, lifecycle.ClickedCount);
        Assert.Equal(1, lifecycle.ClosedCount);
    }

    [Fact]
    public void PopupUnavailable_FallsBackToExistingTrayBalloon()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        fixture.Popup.TryShowResult = false;
        LifecycleProbe lifecycle = new();

        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Assert.Equal(1, fixture.Tray.BalloonCount);
        Assert.Equal(1, lifecycle.ShownCount);
        fixture.Tray.RaiseBalloonClosed();
        Assert.Equal(1, lifecycle.ClosedCount);
    }

    [Fact]
    public void PopupPreviewEnabled_ReceivesWebNotificationTitleAndBody()
    {
        using NotificationFixture fixture = CreateNotificationFixture();

        fixture.Coordinator.Handle(
            fixture.Request(new LifecycleProbe(), "Preview title", "Preview body"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Preview title", popup.Title);
        Assert.Equal("Preview body", popup.Body);
    }

    [Fact]
    public void PopupPreviewDisabled_DoesNotReceiveWebNotificationTitleOrBody()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        fixture.Settings.Notifications.ShowNotificationPreview = false;

        fixture.Coordinator.Handle(
            fixture.Request(new LifecycleProbe(), "Secret title", "Secret body"));

        NotificationPopupDisplayModel popup = Assert.Single(fixture.Popup.Shown);
        Assert.Equal("Новое сообщение в Telegram", popup.Title);
        Assert.Equal(string.Empty, popup.Body);
        Assert.DoesNotContain("Secret", popup.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_DoesNotCompleteBeforeReportShown()
    {
        LifecycleProbe lifecycle = new();

        lifecycle.Lifecycle.ReportClicked();
        lifecycle.Lifecycle.ReportClosed();

        Assert.Equal(0, lifecycle.ClickedCount);
        Assert.Equal(0, lifecycle.ClosedCount);
        Assert.False(lifecycle.Lifecycle.IsCompleted);
    }

    [Fact]
    public void NotificationQueue_ShowsAtMostThreePopupsThenAdvances()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        List<LifecycleProbe> lifecycles = Enumerable.Range(0, 4).Select(_ => new LifecycleProbe()).ToList();

        foreach (LifecycleProbe lifecycle in lifecycles)
        {
            fixture.Coordinator.Handle(fixture.Request(lifecycle));
        }

        Assert.Equal(3, fixture.Popup.VisibleCount);
        Assert.Equal(1, fixture.Coordinator.PendingCount);

        fixture.Popup.Close(fixture.Popup.Shown[0].NotificationId);

        Assert.Equal(4, fixture.Popup.Shown.Count);
        Assert.Equal(3, fixture.Popup.VisibleCount);
        Assert.Equal(0, fixture.Coordinator.PendingCount);
        Assert.Equal(1, lifecycles[0].ClosedCount);
        Assert.Equal(1, lifecycles[3].ShownCount);
    }

    [Fact]
    public void NotificationQueue_NeverExceedsTwentyPendingItems()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        List<LifecycleProbe> lifecycles = Enumerable.Range(0, 24).Select(_ => new LifecycleProbe()).ToList();

        foreach (LifecycleProbe lifecycle in lifecycles)
        {
            fixture.Coordinator.Handle(fixture.Request(lifecycle));
        }

        Assert.Equal(20, fixture.Coordinator.PendingCount);
        Assert.Equal(3, fixture.Popup.VisibleCount);
        Assert.True(fixture.Coordinator.HasActiveNotification);
        Assert.True(lifecycles[3].Lifecycle.IsCompleted);
    }

    [Fact]
    public void MutingAccount_DiscardsItsPendingNotifications()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        List<LifecycleProbe> lifecycles = Enumerable.Range(0, 4).Select(_ => new LifecycleProbe()).ToList();
        foreach (LifecycleProbe lifecycle in lifecycles)
        {
            fixture.Coordinator.Handle(fixture.Request(lifecycle));
        }

        fixture.Service.IsMuted = true;
        fixture.Coordinator.DiscardPending(fixture.Service.Id);

        Assert.Equal(0, fixture.Popup.VisibleCount);
        Assert.Equal(0, fixture.Coordinator.PendingCount);
        Assert.All(lifecycles, lifecycle => Assert.True(lifecycle.Lifecycle.IsCompleted));
    }

    [Fact]
    public void EnablingDnd_ClearsNotificationQueue()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe first = new();
        LifecycleProbe second = new();
        fixture.Coordinator.Handle(fixture.Request(first));
        fixture.Coordinator.Handle(fixture.Request(second));

        fixture.Coordinator.OnDoNotDisturbChanged(enabled: true);

        Assert.False(fixture.Coordinator.HasActiveNotification);
        Assert.Equal(0, fixture.Coordinator.PendingCount);
        Assert.True(first.Lifecycle.IsCompleted);
        Assert.True(second.Lifecycle.IsCompleted);
        Assert.True(fixture.Service.HasUnreadActivity);
    }

    [Fact]
    public void Exit_ClearsNotificationQueue()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe first = new();
        LifecycleProbe second = new();
        fixture.Coordinator.Handle(fixture.Request(first));
        fixture.Coordinator.Handle(fixture.Request(second));

        fixture.Coordinator.Shutdown();

        Assert.False(fixture.Coordinator.HasActiveNotification);
        Assert.Equal(0, fixture.Coordinator.PendingCount);
        Assert.Equal(0, fixture.Popup.VisibleCount);
        Assert.True(first.Lifecycle.IsCompleted);
        Assert.True(second.Lifecycle.IsCompleted);
    }

    [Fact]
    public void NotificationAfterShutdown_IsCompletedWithoutCreatingPopupUi()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        LifecycleProbe lifecycle = new();
        fixture.Coordinator.Shutdown();

        fixture.Coordinator.Handle(fixture.Request(lifecycle));

        Assert.Empty(fixture.Popup.Shown);
        Assert.Equal(0, fixture.Popup.VisibleCount);
        Assert.True(lifecycle.Lifecycle.IsCompleted);
    }

    [Fact]
    public void NotificationContent_IsNeverPersistedInApplicationSettings()
    {
        using NotificationFixture fixture = CreateNotificationFixture();
        const string secretTitle = "Never persist title 24f1";
        const string secretBody = "Never persist body 6a02";
        fixture.Coordinator.Handle(
            fixture.Request(new LifecycleProbe(), secretTitle, secretBody));

        string json = JsonSerializer.Serialize(fixture.Settings);

        Assert.DoesNotContain(secretTitle, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBody, json, StringComparison.Ordinal);
    }

    [Fact]
    public void TelegramNotificationReceived_PlaysOneSystemSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();

        Assert.True(fixture.Coordinator.RequestSound(fixture.Request()));
        Assert.Equal(0, fixture.Player.PlayCount);

        fixture.Dispatcher.RunAll();

        Assert.Equal(1, fixture.Player.PlayCount);
    }

    [Fact]
    public void TwoRapidTelegramNotifications_PlayOneSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();

        Assert.True(fixture.Coordinator.RequestSound(fixture.Request()));
        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();
        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(1, fixture.Player.PlayCount);
    }

    [Fact]
    public void TelegramNotificationAfterCooldown_PlaysNewSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        Assert.True(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        fixture.Time.Advance(TelegramNotificationSoundCoordinator.SoundCooldown);
        Assert.True(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(2, fixture.Player.PlayCount);
    }

    [Fact]
    public void DoNotDisturb_SuppressesTelegramNotificationSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        fixture.Settings.Notifications.DoNotDisturb = true;

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void MutedTelegramAccount_SuppressesNotificationSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        fixture.Service.IsMuted = true;

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void NotificationsDisabled_SuppressesTelegramNotificationSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        fixture.Settings.Notifications.IsEnabled = false;

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void SoundPreferenceDisabled_SuppressesTelegramNotificationSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        fixture.Settings.Notifications.PlaySound = false;

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Theory]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    [InlineData(ServiceType.VkMessenger)]
    public void OtherServices_DoNotUseTelegramNotificationSound(ServiceType serviceType)
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture(serviceType);

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void Shutdown_CancelsPendingTelegramNotificationSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        Assert.True(fixture.Coordinator.RequestSound(fixture.Request()));

        fixture.Coordinator.Shutdown();
        fixture.Dispatcher.RunAll();

        Assert.True(fixture.Coordinator.IsShutdownStarted);
        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void NotificationAfterShutdown_DoesNotCreateSound()
    {
        using TelegramSoundFixture fixture = CreateTelegramSoundFixture();
        fixture.Coordinator.Shutdown();

        Assert.False(fixture.Coordinator.RequestSound(fixture.Request()));
        fixture.Dispatcher.RunAll();

        Assert.Equal(0, fixture.Player.PlayCount);
    }

    [Fact]
    public void WebViewSessionManager_DoesNotExposeNotificationAudioMuting()
    {
        Assert.Null(typeof(WebViewSessionManager).GetMethod("TrySetMuted"));
        Assert.DoesNotContain(
            typeof(WebViewSessionManager).GetEvents(),
            eventInfo => eventInfo.Name.Contains("Audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfficialOrigin_CanRequestNotificationPermissionOnlyOnce()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        service.NotificationPermissionState = NotificationPermissionState.Unknown;
        AppSettings settings = new() { Services = [service] };
        FakeSettingsStore store = new(settings);
        FakePermissionPrompt prompt = new(true);
        using NotificationPermissionCoordinator coordinator = new(
            new NavigationPolicy(_catalog),
            prompt,
            new ImmediateDispatcher(),
            store);

        NotificationPermissionState first = await coordinator.DecideAsync(service, "https://web.telegram.org/");
        NotificationPermissionState second = await coordinator.DecideAsync(service, "https://web.telegram.org/");

        Assert.Equal(NotificationPermissionState.Allowed, first);
        Assert.Equal(NotificationPermissionState.Allowed, second);
        Assert.Equal(1, prompt.ShowCount);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task ExternalOrigin_DoesNotReceiveNotificationPermission()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        service.NotificationPermissionState = NotificationPermissionState.Unknown;
        AppSettings settings = new() { Services = [service] };
        FakeSettingsStore store = new(settings);
        FakePermissionPrompt prompt = new(true);
        using NotificationPermissionCoordinator coordinator = new(
            new NavigationPolicy(_catalog),
            prompt,
            new ImmediateDispatcher(),
            store);

        NotificationPermissionState result = await coordinator.DecideAsync(service, "https://example.com/");

        Assert.Equal(NotificationPermissionState.Denied, result);
        Assert.Equal(NotificationPermissionState.Unknown, service.NotificationPermissionState);
        Assert.Equal(0, prompt.ShowCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task PermissionDecisions_AreStoredSeparatelyPerAccount()
    {
        ServiceInstance first = CreateService(ServiceType.Telegram);
        ServiceInstance second = CreateService(ServiceType.Telegram);
        first.NotificationPermissionState = NotificationPermissionState.Unknown;
        second.NotificationPermissionState = NotificationPermissionState.Unknown;
        AppSettings settings = new() { Services = [first, second] };
        FakeSettingsStore store = new(settings);
        FakePermissionPrompt prompt = new(true, false);
        using NotificationPermissionCoordinator coordinator = new(
            new NavigationPolicy(_catalog),
            prompt,
            new ImmediateDispatcher(),
            store);

        await coordinator.DecideAsync(first, "https://web.telegram.org/");
        await coordinator.DecideAsync(second, "https://web.telegram.org/");

        Assert.Equal(NotificationPermissionState.Allowed, first.NotificationPermissionState);
        Assert.Equal(NotificationPermissionState.Denied, second.NotificationPermissionState);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void WebViewSubscriptionGuard_PreventsDuplicateSubscriptionsAfterRecreation()
    {
        WebViewEventSubscriptionGuard guard = new();

        Assert.True(guard.TrySubscribe());
        Assert.False(guard.TrySubscribe());
        Assert.True(guard.TryUnsubscribe());
        Assert.False(guard.TryUnsubscribe());
        Assert.True(guard.TrySubscribe());
    }

    [Fact]
    public void WebViewEventCoordinator_UnsubscribesDeterministically()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        AppSettings settings = new() { Services = [service] };
        FakeSettingsStore store = new(settings);
        FakeSessionManager sessions = new();
        ServiceActivityCoordinator activity = new();
        FakeWebNotificationCoordinator notifications = new();
        WebViewEventCoordinator coordinator = new(
            sessions,
            store,
            activity,
            notifications,
            new FakeWindowActivationService());

        sessions.RaiseDocumentTitle(service, "(3) Telegram");
        coordinator.Dispose();
        sessions.RaiseDocumentTitle(service, "(9) Telegram");

        Assert.Equal(3, service.UnreadCount);
    }

    [Fact]
    public void VkExactOnlyPolicy_RemainsUnchanged()
    {
        NavigationPolicy policy = new(_catalog);

        Assert.True(policy.IsAllowedTopLevelNavigation(ServiceType.VkMessenger, new Uri("https://id.vk.ru/")));
        Assert.False(policy.IsAllowedTopLevelNavigation(ServiceType.VkMessenger, new Uri("https://sub.id.vk.ru/")));
    }

    private static ApplicationTrayCoordinator CreateTrayCoordinator(
        FakeSettingsStore store,
        ServiceActivityCoordinator activity,
        MainWindowViewModel viewModel,
        FakeTaskbarActivityIndicator taskbar) =>
        new(
            new FakeTrayIconService(),
            new FakeWindowActivationService(),
            new ApplicationExitCoordinator(),
            new FakeWebNotificationCoordinator(),
            activity,
            store,
            viewModel,
            new ImmediateDispatcher(),
            taskbar);

    private MainWindowViewModel CreateViewModel(
        AppSettings settings,
        FakeSettingsStore store,
        IServiceActivityCoordinator? activity = null,
        IWebNotificationCoordinator? notificationCoordinator = null)
    {
        MainWindowViewModel viewModel = new(
            _catalog,
            new FakeSessionManager(),
            store,
            activity ?? new ServiceActivityCoordinator(),
            notificationCoordinator ?? new FakeWebNotificationCoordinator());
        viewModel.Initialize(settings);
        return viewModel;
    }

    private ServiceInstance CreateService(ServiceType serviceType)
    {
        Guid id = Guid.NewGuid();
        ServiceDefinition definition = _catalog.Get(serviceType);
        return new ServiceInstance
        {
            Id = id,
            ServiceType = serviceType,
            DisplayName = definition.DisplayName,
            StartUrl = definition.StartUri?.AbsoluteUri,
            ProfileName = ProfileNameFactory.Create(id),
            IsEnabled = true,
            NotificationPermissionState = NotificationPermissionState.Allowed
        };
    }

    private NotificationFixture CreateNotificationFixture()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        AppSettings settings = new() { Services = [service] };
        return new NotificationFixture(settings, service, _catalog);
    }

    private TelegramSoundFixture CreateTelegramSoundFixture(ServiceType serviceType = ServiceType.Telegram)
    {
        ServiceInstance service = CreateService(serviceType);
        AppSettings settings = new() { Services = [service] };
        return new TelegramSoundFixture(settings, service);
    }

    private sealed class NotificationFixture : IDisposable
    {
        public NotificationFixture(AppSettings settings, ServiceInstance service, BuiltInServiceCatalog catalog)
        {
            Settings = settings;
            Service = service;
            Store = new FakeSettingsStore(settings);
            Tray = new FakeTrayIconService();
            Window = new FakeWindowActivationService();
            Activity = new ServiceActivityCoordinator();
            Popup = new FakeNotificationPopupService();
            Sound = new FakeTelegramNotificationSoundCoordinator();
            Coordinator = new WebNotificationCoordinator(
                Popup,
                Tray,
                Window,
                Store,
                new NavigationPolicy(catalog),
                Activity,
                catalog,
                Sound);
        }

        public AppSettings Settings { get; }
        public ServiceInstance Service { get; }
        public FakeSettingsStore Store { get; }
        public FakeTrayIconService Tray { get; }
        public FakeNotificationPopupService Popup { get; }
        public FakeWindowActivationService Window { get; }
        public ServiceActivityCoordinator Activity { get; }
        public FakeTelegramNotificationSoundCoordinator Sound { get; }
        public WebNotificationCoordinator Coordinator { get; }

        public WebNotificationRequest Request(
            LifecycleProbe lifecycle,
            string title = "Private title",
            string body = "Private body") =>
            new(Service.Id, "https://web.telegram.org/", title, body, lifecycle.Lifecycle);

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class TelegramSoundFixture : IDisposable
    {
        public TelegramSoundFixture(AppSettings settings, ServiceInstance service)
        {
            Settings = settings;
            Service = service;
            Store = new FakeSettingsStore(settings);
            Player = new FakeNotificationSoundPlayer();
            Dispatcher = new QueuedDispatcher();
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));
            Coordinator = new TelegramNotificationSoundCoordinator(
                Store,
                Player,
                Dispatcher,
                Time);
        }

        public AppSettings Settings { get; }
        public ServiceInstance Service { get; }
        public FakeSettingsStore Store { get; }
        public FakeNotificationSoundPlayer Player { get; }
        public QueuedDispatcher Dispatcher { get; }
        public FakeTimeProvider Time { get; }
        public TelegramNotificationSoundCoordinator Coordinator { get; }

        public TelegramNotificationSoundRequest Request() =>
            new(Service.Id, Service.ServiceType);

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class LifecycleProbe
    {
        public LifecycleProbe()
        {
            Lifecycle = new WebNotificationLifecycle(
                () => ShownCount++,
                () => ClickedCount++,
                () => ClosedCount++);
        }

        public WebNotificationLifecycle Lifecycle { get; }
        public int ShownCount { get; private set; }
        public int ClickedCount { get; private set; }
        public int ClosedCount { get; private set; }
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IApplicationSettingsStore
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

    private sealed class FakeTrayIconService : ITrayIconService
    {
        public event EventHandler? OpenRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? DoNotDisturbToggleRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? BalloonClicked;
        public event EventHandler? BalloonClosed;

        public bool DoNotDisturb { get; private set; }
        public string ToolTip { get; private set; } = string.Empty;
        public int BalloonCount { get; private set; }
        public bool ShowBalloonResult { get; set; } = true;
        public int ShutdownCount { get; private set; }
        public bool IsShutdown { get; private set; }

        public void Show(bool doNotDisturb, string toolTipText)
        {
            DoNotDisturb = doNotDisturb;
            ToolTip = toolTipText;
        }

        public void SetDoNotDisturb(bool enabled) => DoNotDisturb = enabled;
        public void SetToolTip(string text) => ToolTip = text;

        public void BeginShutdown()
        {
            if (IsShutdown)
            {
                return;
            }

            IsShutdown = true;
            ShutdownCount++;
        }

        public bool TryShowBalloon(string title, string text, int timeoutMilliseconds = 5000)
        {
            BalloonCount++;
            return ShowBalloonResult;
        }

        public void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseDoNotDisturbToggle() => DoNotDisturbToggleRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseBalloonClicked() => BalloonClicked?.Invoke(this, EventArgs.Empty);
        public void RaiseBalloonClosed() => BalloonClosed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => BeginShutdown();
    }

    private sealed class FakeNotificationPopupService : INotificationPopupService
    {
        private readonly Dictionary<Guid, NotificationPopupDisplayModel> _visible = [];

        public event EventHandler<NotificationPopupEventArgs>? Clicked;
        public event EventHandler<NotificationPopupEventArgs>? Closed;

        public bool TryShowResult { get; set; } = true;
        public int VisibleCount => _visible.Count;
        public List<NotificationPopupDisplayModel> Shown { get; } = [];

        public bool TryShow(NotificationPopupDisplayModel notification)
        {
            if (!TryShowResult)
            {
                return false;
            }

            _visible.Add(notification.NotificationId, notification);
            Shown.Add(notification);
            return true;
        }

        public void Close(Guid notificationId)
        {
            if (_visible.Remove(notificationId))
            {
                Closed?.Invoke(this, new NotificationPopupEventArgs(notificationId));
            }
        }

        public void CloseAll()
        {
            foreach (Guid notificationId in _visible.Keys.ToArray())
            {
                Close(notificationId);
            }
        }

        public void RaiseClicked(Guid notificationId)
        {
            if (_visible.Remove(notificationId))
            {
                Clicked?.Invoke(this, new NotificationPopupEventArgs(notificationId));
            }
        }

        public void Dispose() => CloseAll();
    }

    private sealed class FakeTaskbarActivityIndicator : ITaskbarActivityIndicator
    {
        public bool HasActivity { get; private set; }
        public void Attach(System.Windows.Window window) { }
        public void Detach(System.Windows.Window window) { }
        public void SetHasActivity(bool hasActivity) => HasActivity = hasActivity;
    }

    private sealed class FakeWindowActivationService : IWindowActivationService
    {
        public bool IsMainWindowActive { get; set; }
        public bool IsMainWindowVisible { get; set; }
        public Guid? SelectedServiceId { get; set; }
        public int ActivationCount { get; private set; }
        public List<Guid> ActivatedServiceIds { get; } = [];

        public void Attach(System.Windows.Window window, Func<Guid?> selectedServiceId, Action<Guid> selectService) { }
        public void Detach(System.Windows.Window window) { }

        public void ShowAndActivate(Guid? serviceInstanceId = null)
        {
            ActivationCount++;
            if (serviceInstanceId is Guid id)
            {
                ActivatedServiceIds.Add(id);
                SelectedServiceId = id;
            }
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public void RunAll()
        {
            while (_pending.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class FakePermissionPrompt(params bool[] decisions) : INotificationPermissionPrompt
    {
        private readonly Queue<bool> _decisions = new(decisions);
        public int ShowCount { get; private set; }

        public bool Show(string accountDisplayName)
        {
            ShowCount++;
            return _decisions.Dequeue();
        }
    }

    private sealed class FakeWebNotificationCoordinator : IWebNotificationCoordinator
    {
        public int PendingCount => 0;
        public bool HasActiveNotification => false;
        public bool WasShutdown { get; private set; }
        public int ShutdownCount { get; private set; }
        public List<bool> DoNotDisturbChanges { get; } = [];
        public List<WebNotificationRequest> Requests { get; } = [];
        public List<Guid> DiscardedServiceIds { get; } = [];

        public void Handle(WebNotificationRequest request) => Requests.Add(request);
        public void DiscardPending(Guid serviceInstanceId) => DiscardedServiceIds.Add(serviceInstanceId);
        public void OnDoNotDisturbChanged(bool enabled) => DoNotDisturbChanges.Add(enabled);
        public void Shutdown()
        {
            if (WasShutdown)
            {
                return;
            }

            WasShutdown = true;
            ShutdownCount++;
        }
        public void Dispose() { }
    }

    private sealed class FakeTelegramNotificationSoundCoordinator : ITelegramNotificationSoundCoordinator
    {
        public bool IsShutdownStarted { get; private set; }
        public List<TelegramNotificationSoundRequest> Requests { get; } = [];

        public bool RequestSound(TelegramNotificationSoundRequest request)
        {
            Requests.Add(request);
            return true;
        }

        public void Shutdown() => IsShutdownStarted = true;
        public void Dispose() => Shutdown();
    }

    private sealed class FakeNotificationSoundPlayer : INotificationSoundPlayer
    {
        public int PlayCount { get; private set; }
        public bool TryPlay(ServiceType serviceType, NotificationSoundMode mode)
        {
            PlayCount++;
            return true;
        }
    }


    private sealed class FakeSessionManager : IWebViewSessionManager
    {
        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested
        {
            add { }
            remove { }
        }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged;
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived;

        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted { get; private set; }
        public int InitializedSessionCount => 0;
        public int InitialNavigationCount => 0;
        public Task<bool> InitializeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PrimeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public bool IsSessionInitialized(Guid serviceInstanceId) => false;
        public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false) { }
        public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible) { }
        public void NotifyParentWindowPositionChanged() { }
        public bool HasSession(Guid serviceInstanceId) => false;
        public void DeactivateSession() { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId) { }
        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void ReleaseAllSessions() { }
        public void BeginShutdown() => IsShutdownStarted = true;
        public void Dispose() { }

        public void RaiseDocumentTitle(ServiceInstance service, string title) =>
            DocumentTitleChanged?.Invoke(
                this,
                new ServiceDocumentTitleChangedEventArgs(service.Id, service.ServiceType, title));

        public void RaiseNotification(ServiceInstance service, IWebNotificationLifecycle lifecycle) =>
            NotificationReceived?.Invoke(
                this,
                new WebNotificationReceivedEventArgs(
                    service.Id,
                    service.ServiceType,
                    service.StartUrl ?? string.Empty,
                    "Private title",
                    "Private body",
                    lifecycle));
    }

    private sealed class TempSettingsFolder : IDisposable
    {
        public TempSettingsFolder()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        }

        public string DirectoryPath { get; }
        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
