using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class DpapiMailCredentialProtector : IMailCredentialProtector
{
    private static readonly byte[] OptionalEntropy = "UnifiedMessenger.MailCredentials.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), OptionalEntropy, DataProtectionScope.CurrentUser);
}

public sealed class FileMailCredentialStore : IMailCredentialStore
{
    private readonly string _credentialsFolder;
    private readonly IMailCredentialProtector _protector;

    public FileMailCredentialStore(IAppPaths appPaths, IMailCredentialProtector protector)
        : this(appPaths?.MailCredentialsFolder ?? throw new ArgumentNullException(nameof(appPaths)), protector)
    {
    }

    public FileMailCredentialStore(string credentialsFolder, IMailCredentialProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsFolder);
        _credentialsFolder = Path.GetFullPath(credentialsFolder);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public async Task SaveAsync(
        string credentialKey,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        string targetPath = GetCredentialPath(credentialKey);
        Directory.CreateDirectory(_credentialsFolder);
        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = _protector.Protect(plaintext);
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string?> LoadAsync(
        string credentialKey,
        CancellationToken cancellationToken = default)
    {
        string path = GetCredentialPath(credentialKey);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetCredentialPath(credentialKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetCredentialPath(string credentialKey)
    {
        if (!Guid.TryParseExact(credentialKey, "N", out _))
        {
            throw new ArgumentException("Credential keys must be GUIDs in N format.", nameof(credentialKey));
        }

        string path = Path.GetFullPath(Path.Combine(_credentialsFolder, credentialKey + ".credential.bin"));
        string folderPrefix = _credentialsFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Credential path escaped its storage folder.");
        }

        return path;
    }
}
