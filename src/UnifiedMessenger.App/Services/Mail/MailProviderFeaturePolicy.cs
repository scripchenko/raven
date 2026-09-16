using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public readonly record struct MailProviderFeaturePolicy(
    bool UsesYandexPresentation,
    bool IsManagedImap,
    bool SupportsAppPasswordReplacement,
    bool IncludeAccountDisplayNameInFromHeader);

public static class MailProviderFeaturePolicies
{
    public static MailProviderFeaturePolicy Get(MailProviderType provider) => provider switch
    {
        MailProviderType.Yandex => new(
            UsesYandexPresentation: true,
            IsManagedImap: true,
            SupportsAppPasswordReplacement: true,
            IncludeAccountDisplayNameInFromHeader: true),
        MailProviderType.MailRu => new(
            UsesYandexPresentation: false,
            IsManagedImap: true,
            SupportsAppPasswordReplacement: true,
            IncludeAccountDisplayNameInFromHeader: false),
        _ => new(
            UsesYandexPresentation: false,
            IsManagedImap: false,
            SupportsAppPasswordReplacement: false,
            IncludeAccountDisplayNameInFromHeader: true)
    };
}
