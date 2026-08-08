using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.ViewModels;

public sealed class SettingsAccountEventArgs(ServiceInstance service) : EventArgs
{
    public ServiceInstance Service { get; } = service;
}

public sealed class SettingsAccountEnabledEventArgs(ServiceInstance service, bool isEnabled) : EventArgs
{
    public ServiceInstance Service { get; } = service;
    public bool IsEnabled { get; } = isEnabled;
}
