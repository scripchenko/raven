using System.IO;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class GmailReauthenticationService(
    IMailCredentialStore credentialStore,
    IGmailOAuthService oauthService) : IGmailReauthenticationService
{
    public async Task<GmailReauthenticationResult> ReauthenticateAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider is not MailProviderType.Gmail)
        {
            return GmailReauthenticationResult.Failure();
        }

        string scope = await GetRequiredScopeAsync(account, cancellationToken);
        GmailOAuthAuthorizationResult authorization = await oauthService.AuthorizeAsync(scope, cancellationToken);
        if (!authorization.IsSuccess || authorization.Session is not GmailOAuthSession session)
        {
            return authorization.FailureKind is MailConnectionFailureKind.OperationCanceled
                or MailConnectionFailureKind.OAuthDenied
                ? GmailReauthenticationResult.Canceled()
                : GmailReauthenticationResult.Failure();
        }

        GmailProfileResult profileResult = await oauthService.GetProfileAsync(session, cancellationToken);
        if (!profileResult.IsSuccess || profileResult.Profile is not GmailUserProfile profile)
        {
            return cancellationToken.IsCancellationRequested
                ? GmailReauthenticationResult.Canceled()
                : GmailReauthenticationResult.Failure();
        }

        if (!string.Equals(
                NormalizeEmail(account.EmailAddress),
                NormalizeEmail(profile.EmailAddress),
                StringComparison.OrdinalIgnoreCase))
        {
            return GmailReauthenticationResult.WrongAccount(account.EmailAddress);
        }

        try
        {
            await credentialStore.SaveAsync(
                account.CredentialKey,
                session.PersistentCredential,
                cancellationToken);
            return GmailReauthenticationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GmailReauthenticationResult.Canceled();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException)
        {
            return GmailReauthenticationResult.Failure();
        }
    }

    private async Task<string> GetRequiredScopeAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        try
        {
            MailCredential? credential = await credentialStore.LoadAsync(
                account.CredentialKey,
                cancellationToken);
            return credential is null || credential.HasGmailModifyScope
                ? GmailOAuthConstants.ModifyScope
                : GmailOAuthConstants.ReadOnlyScope;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException)
        {
            return GmailOAuthConstants.ModifyScope;
        }
    }

    private static string NormalizeEmail(string email) => email.Trim();
}
