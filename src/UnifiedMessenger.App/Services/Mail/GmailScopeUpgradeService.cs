using System.IO;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class GmailScopeUpgradeService(
    IMailCredentialStore credentialStore,
    IGmailOAuthService oauthService) : IGmailScopeUpgradeService
{
    public async Task<GmailScopeUpgradeResult> UpgradeAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider is not MailProviderType.Gmail)
        {
            return GmailScopeUpgradeResult.Failure("Изменение разрешений доступно только для Gmail.");
        }

        GmailOAuthAuthorizationResult authorization = await oauthService.AuthorizeAsync(
            GmailOAuthConstants.ModifyScope,
            cancellationToken);
        if (!authorization.IsSuccess || authorization.Session is not GmailOAuthSession session)
        {
            return GmailScopeUpgradeResult.Failure(authorization.UserMessage);
        }

        GmailProfileResult profileResult = await oauthService.GetProfileAsync(session, cancellationToken);
        if (!profileResult.IsSuccess || profileResult.Profile is not GmailUserProfile profile)
        {
            return GmailScopeUpgradeResult.Failure(profileResult.UserMessage);
        }

        if (!string.Equals(
                NormalizeEmail(account.EmailAddress),
                NormalizeEmail(profile.EmailAddress),
                StringComparison.OrdinalIgnoreCase))
        {
            return GmailScopeUpgradeResult.Failure("Вы вошли в другой Google-аккаунт.");
        }

        if (!session.PersistentCredential.HasGmailModifyScope)
        {
            return GmailScopeUpgradeResult.Failure("Google не предоставил разрешение на изменение статуса писем.");
        }

        try
        {
            // FileMailCredentialStore replaces the DPAPI blob atomically. Until this point
            // the existing readonly credential has not been touched.
            await credentialStore.SaveAsync(
                account.CredentialKey,
                session.PersistentCredential,
                cancellationToken);
            return GmailScopeUpgradeResult.Success();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException)
        {
            return GmailScopeUpgradeResult.Failure(
                "Не удалось безопасно сохранить новое разрешение Google.");
        }
    }

    internal static string NormalizeEmail(string email) => email.Trim();
}
