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
    private readonly SemaphoreSlim _stateGate = new(1, 1);
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

        ServiceInstance? persistedService = FindPersistedService(service);
        if (persistedService is null)
        {
            return NotificationPermissionState.Denied;
        }

        if (persistedService.NotificationPermissionState is not NotificationPermissionState.Unknown)
        {
            return persistedService.NotificationPermissionState;
        }

        await _stateGate.WaitAsync(cancellationToken);
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
            _stateGate.Release();
        }
    }

    public async Task<NotificationPermissionState> SynchronizeFromProfileAsync(
        ServiceInstance service,
        string? permissionOrigin,
        NotificationPermissionState profileState,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(service);

        if (!Enum.IsDefined(profileState))
        {
            throw new ArgumentOutOfRangeException(nameof(profileState), profileState, "Unknown notification permission state.");
        }

        if (!Uri.TryCreate(permissionOrigin, UriKind.Absolute, out Uri? origin)
            || !navigationPolicy.IsAllowedTopLevelNavigation(service.ServiceType, origin))
        {
            return NotificationPermissionState.Denied;
        }

        ServiceInstance? persistedService = FindPersistedService(service);
        if (persistedService is null)
        {
            return NotificationPermissionState.Denied;
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (persistedService.NotificationPermissionState == profileState)
            {
                return profileState;
            }

            persistedService.NotificationPermissionState = profileState;
            await settingsStore.SaveAsync(cancellationToken);
            return profileState;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private ServiceInstance? FindPersistedService(ServiceInstance service) =>
        settingsStore.Current.Services.FirstOrDefault(
            candidate => candidate.Id == service.Id
                && candidate.ServiceType == service.ServiceType
                && string.Equals(candidate.ProfileName, service.ProfileName, StringComparison.Ordinal));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateGate.Dispose();
    }
}
