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

public sealed class SettingsMailAccountEventArgs(MailAccount account) : EventArgs
{
    public MailAccount Account { get; } = account;
}

public sealed class SettingsMailAccountEnabledEventArgs(MailAccount account, bool isEnabled) : EventArgs
{
    public MailAccount Account { get; } = account;
    public bool IsEnabled { get; } = isEnabled;
}
