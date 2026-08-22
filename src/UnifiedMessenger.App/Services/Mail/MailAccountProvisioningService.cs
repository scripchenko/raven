using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailAccountProvisioningService(
    IMailProviderFactory providerFactory,
    IMailCredentialStore credentialStore,
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
                "Введите адрес электронной почты.");
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
                    "Этот почтовый аккаунт уже добавлен.");
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
                await credentialStore.SaveAsync(credentialKey, secret, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                return MailAccountProvisioningResult.Failure(
                    MailConnectionFailureKind.CredentialStorageFailed,
                    "Не удалось безопасно сохранить учётные данные Windows.");
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
                    "Не удалось сохранить настройки почтового аккаунта.");
            }

            return MailAccountProvisioningResult.Success(account);
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
