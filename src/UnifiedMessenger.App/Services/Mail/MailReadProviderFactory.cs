using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailReadProviderFactory(
    IEnumerable<IMailReadProvider> providers,
    IGmailScopeUpgradeService? gmailScopeUpgradeService = null,
    IGmailReauthenticationService? gmailReauthenticationService = null,
    IGmailMailboxManagementService? gmailMailboxManagementService = null)
    : IMailReadProviderFactory
{
    private readonly IReadOnlyList<IMailReadProvider> _providers = providers.ToArray();

    public IGmailScopeUpgradeService? GmailScopeUpgradeService { get; } = gmailScopeUpgradeService;
    public IGmailReauthenticationService? GmailReauthenticationService { get; } = gmailReauthenticationService;
    public IGmailMailboxManagementService? GmailMailboxManagementService { get; } = gmailMailboxManagementService;

    public IMailReadProvider Get(MailProviderType providerType)
    {
        IMailReadProvider[] matches = _providers.Where(provider => provider.Supports(providerType)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new KeyNotFoundException(
                $"Mail read provider for '{providerType}' is not registered uniquely.");
    }
}
