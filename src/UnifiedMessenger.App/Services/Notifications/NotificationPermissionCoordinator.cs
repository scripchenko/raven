using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class NotificationPermissionCoordinator(
    NavigationPolicy navigationPolicy,
    INotificationPermissionPrompt prompt,
    IUiDispatcher uiDispatcher,
    IApplicationSettingsStore settingsStore) : INotificationPermissionCoordinator, IDisposable
{
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private bool _disposed;

    public async Task<NotificationPermissionState> DecideAsync(
        ServiceInstance service,
        string? senderOrigin,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(service);

        if (!Uri.TryCreate(senderOrigin, UriKind.Absolute, out Uri? origin)
            || !navigationPolicy.IsAllowedTopLevelNavigation(service.ServiceType, origin))
        {
            return NotificationPermissionState.Denied;
        }

        ServiceInstance? persistedService = settingsStore.Current.Services.FirstOrDefault(
            candidate => candidate.Id == service.Id
                && candidate.ServiceType == service.ServiceType
                && string.Equals(candidate.ProfileName, service.ProfileName, StringComparison.Ordinal));
        if (persistedService is null)
        {
            return NotificationPermissionState.Denied;
        }

        if (persistedService.NotificationPermissionState is not NotificationPermissionState.Unknown)
        {
            return persistedService.NotificationPermissionState;
        }

        await _promptGate.WaitAsync(cancellationToken);
        try
        {
            if (persistedService.NotificationPermissionState is not NotificationPermissionState.Unknown)
            {
                return persistedService.NotificationPermissionState;
            }

            bool allowed = await uiDispatcher.InvokeAsync(() => prompt.Show(persistedService.DisplayName));
            persistedService.NotificationPermissionState = allowed
                ? NotificationPermissionState.Allowed
                : NotificationPermissionState.Denied;
            await settingsStore.SaveAsync(cancellationToken);
            return persistedService.NotificationPermissionState;
        }
        finally
        {
            _promptGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _promptGate.Dispose();
    }
}
