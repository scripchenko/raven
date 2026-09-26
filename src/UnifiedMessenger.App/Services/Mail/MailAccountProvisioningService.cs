using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailAccountProvisioningService(
    IMailProviderFactory providerFactory,
    IMailCredentialStore credentialStore,
    IGmailOAuthService gmailOAuthService,
    IApplicationSettingsStore settingsStore,
    TimeProvider timeProvider) : IMailAccountProvisioningService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<MailAccountProvisioningResult> ConnectAsync(
        MailAccountConnectionRequest request,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string normalizedEmail = request.EmailAddress.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return MailAccountProvisioningResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("Enter an email address."));
        }

        IMailProvider provider = providerFactory.Get(request.Provider);
        MailConnectionValidationResult validation = await provider.ValidateAsync(
            request with { EmailAddress = normalizedEmail },
            secret,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            return MailAccountProvisioningResult.Failure(validation.FailureKind, validation.UserMessage);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            AppSettings settings = settingsStore.Current;
            if (settings.MailAccounts.Any(account =>
                account.Provider == request.Provider
                && string.Equals(account.EmailAddress, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.AlreadyExists,
                    L.Instance.Get("This mail account has already been added."));
            }

            string credentialKey = Guid.NewGuid().ToString("N");
            MailAccount account = new()
            {
                Id = Guid.NewGuid(),
                Provider = request.Provider,
                EmailAddress = validation.Identity?.EmailAddress ?? normalizedEmail,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? validation.Identity?.DisplayName
                    : request.DisplayName.Trim(),
                IsEnabled = true,
                CredentialKey = credentialKey,
                AuthenticationKind = provider.Descriptor.AuthenticationKind,
                LastSuccessfulConnectionUtc = timeProvider.GetUtcNow(),
                GenericConnectionSettings = request.Provider == MailProviderType.GenericImap
                    ? request.GenericConnectionSettings?.Clone()
                    : null,
                SortOrder = settings.MailAccounts.Count
            };

            try
            {
                await credentialStore.SaveAsync(
                    credentialKey,
                    MailCredential.CreatePassword(secret),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.CredentialStorageFailed,
                    L.Instance.Get("Could not safely save Windows credentials."));
            }

            settings.MailAccounts.Add(account);
            try
            {
                await settingsStore.SaveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                settings.MailAccounts.Remove(account);
                await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
                throw;
            }
            catch
            {
                settings.MailAccounts.Remove(account);
                await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.PersistenceFailed,
                    L.Instance.Get("Could not save mail account settings."));
            }

            return MailAccountProvisioningResult.Success(account);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MailAccountProvisioningResult> ConnectGmailAsync(
        CancellationToken cancellationToken = default)
    {
        GmailOAuthAuthorizationResult authorization = await gmailOAuthService.AuthorizeAsync(
            GmailOAuthConstants.ModifyScope,
            cancellationToken);
        if (!authorization.IsSuccess || authorization.Session is not GmailOAuthSession session)
        {
            return MailAccountProvisioningResult.Failure(
                authorization.FailureKind,
                authorization.UserMessage);
        }

        string credentialKey = Guid.NewGuid().ToString("N");
        try
        {
            await credentialStore.SaveAsync(
                credentialKey,
                session.PersistentCredential,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException)
        {
            return MailAccountProvisioningResult.Failure(
                MailConnectionFailureKind.CredentialStorageFailed,
                L.Instance.Get("Could not safely save the Gmail OAuth credential in Windows."));
        }

        GmailProfileResult profileResult;
        try
        {
            profileResult = await gmailOAuthService.GetProfileAsync(session, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
            throw;
        }

        if (!profileResult.IsSuccess || profileResult.Profile is not GmailUserProfile profile)
        {
            await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
            return MailAccountProvisioningResult.Failure(
                profileResult.FailureKind,
                profileResult.UserMessage);
        }

        string normalizedEmail = profile.EmailAddress.Trim();
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
            throw;
        }

        try
        {
            AppSettings settings = settingsStore.Current;
            if (settings.MailAccounts.Any(account =>
                account.Provider == MailProviderType.Gmail
                && string.Equals(account.EmailAddress, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.AlreadyExists,
                    L.Instance.Get("This Gmail account is already connected."));
            }

            MailAccount account = new()
            {
                Id = Guid.NewGuid(),
                Provider = MailProviderType.Gmail,
                EmailAddress = normalizedEmail,
                IsEnabled = true,
                CredentialKey = credentialKey,
                AuthenticationKind = MailAuthenticationKind.OAuth,
                LastSuccessfulConnectionUtc = timeProvider.GetUtcNow(),
                SortOrder = settings.MailAccounts.Count
            };

            settings.MailAccounts.Add(account);
            try
            {
                await settingsStore.SaveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                settings.MailAccounts.Remove(account);
                await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
                throw;
            }
            catch
            {
                settings.MailAccounts.Remove(account);
                await credentialStore.DeleteAsync(credentialKey, CancellationToken.None);
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.PersistenceFailed,
                    L.Instance.Get("Could not save Gmail account settings."));
            }

            return MailAccountProvisioningResult.Success(account);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MailAccountPasswordReplacementResult> ReplaceAppPasswordAsync(
        MailAccount account,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        MailProviderFeaturePolicy policy = MailProviderFeaturePolicies.Get(account.Provider);
        if (!policy.SupportsAppPasswordReplacement
            || account.AuthenticationKind is not MailAuthenticationKind.Password)
        {
            return MailAccountPasswordReplacementResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("App password changes are unavailable for this account."));
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return MailAccountPasswordReplacementResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("Enter a new app password."));
        }

        IMailProvider provider = providerFactory.Get(account.Provider);
        MailConnectionValidationResult validation = await provider.ValidateAsync(
            new MailAccountConnectionRequest(
                account.Provider,
                account.EmailAddress,
                account.DisplayName),
            newPassword,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            return MailAccountPasswordReplacementResult.Failure(
                validation.FailureKind,
                validation.UserMessage);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            MailAccount? storedAccount = settingsStore.Current.MailAccounts.FirstOrDefault(candidate =>
                candidate.Id == account.Id
                && candidate.Provider == account.Provider
                && candidate.AuthenticationKind is MailAuthenticationKind.Password);
            if (storedAccount is null
                || !string.Equals(storedAccount.CredentialKey, account.CredentialKey, StringComparison.Ordinal)
                || !string.Equals(storedAccount.EmailAddress, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
            {
                return MailAccountPasswordReplacementResult.Failure(
                    MailConnectionFailureKind.InvalidConfiguration,
                    L.Instance.Get("Mail account no longer available in raven settings."));
            }

            try
            {
                await credentialStore.SaveAsync(
                    storedAccount.CredentialKey,
                    MailCredential.CreatePassword(newPassword),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException
                or System.Text.Json.JsonException)
            {
                return MailAccountPasswordReplacementResult.Failure(
                    MailConnectionFailureKind.CredentialStorageFailed,
                    L.Instance.Get("Could not safely save the new app password in Windows."));
            }

            return MailAccountPasswordReplacementResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AppSettings settings = settingsStore.Current;
            MailAccount? account = settings.MailAccounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is null)
            {
                return;
            }

            int index = settings.MailAccounts.IndexOf(account);
            settings.MailAccounts.RemoveAt(index);
            try
            {
                await settingsStore.SaveAsync(cancellationToken);
            }
            catch
            {
                settings.MailAccounts.Insert(index, account);
                throw;
            }

            try
            {
                await credentialStore.DeleteAsync(account.CredentialKey, cancellationToken);
            }
            catch
            {
                settings.MailAccounts.Insert(index, account);
                await settingsStore.SaveAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
