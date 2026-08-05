using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services;

public sealed class TelegramServiceProvisioner(IBuiltInServiceCatalog serviceCatalog)
{
    public bool EnsureTelegramInstance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ServiceDefinition definition = serviceCatalog.Get(ServiceType.Telegram);
        ServiceInstance? telegram = settings.Services.FirstOrDefault(
            service => service.ServiceType == ServiceType.Telegram);
        bool changed = false;

        if (telegram is null)
        {
            Guid id = Guid.NewGuid();
            telegram = new ServiceInstance
            {
                Id = id,
                ServiceType = ServiceType.Telegram,
                DisplayName = definition.DisplayName,
                StartUrl = definition.StartUri?.AbsoluteUri,
                ProfileName = ProfileNameFactory.Create(id),
                IsEnabled = true,
                SortOrder = 0
            };
            settings.Services.Add(telegram);
            changed = true;
        }

        if (telegram.Id == Guid.Empty)
        {
            telegram.Id = Guid.NewGuid();
            changed = true;
        }

        string safeProfileName = ProfileNameFactory.Create(telegram.Id);
        if (!string.Equals(telegram.ProfileName, safeProfileName, StringComparison.Ordinal))
        {
            telegram.ProfileName = safeProfileName;
            changed = true;
        }

        string startUrl = definition.StartUri?.AbsoluteUri
            ?? throw new InvalidOperationException("Telegram must have a start URI.");
        if (!string.Equals(telegram.StartUrl, startUrl, StringComparison.Ordinal))
        {
            telegram.StartUrl = startUrl;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(telegram.DisplayName))
        {
            telegram.DisplayName = definition.DisplayName;
            changed = true;
        }

        if (!telegram.IsEnabled)
        {
            telegram.IsEnabled = true;
            changed = true;
        }

        if (settings.LastServiceId != telegram.Id)
        {
            settings.LastServiceId = telegram.Id;
            changed = true;
        }

        return changed;
    }
}
