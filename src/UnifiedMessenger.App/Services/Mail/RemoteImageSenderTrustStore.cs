using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MimeKit;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public interface IRemoteImageSenderTrustStore
{
    Task<bool> IsTrustedAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default);

    Task TrustAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default);

    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IRemoteImageSenderTrustProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

public static class RemoteImageSenderIdentity
{
    public static bool TryNormalize(string? address, out string normalizedAddress)
    {
        normalizedAddress = string.Empty;
        string candidate = address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || !MailboxAddress.TryParse(candidate, out MailboxAddress? mailbox))
        {
            return false;
        }

        string parsedAddress = mailbox.Address?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parsedAddress)
            || !string.Equals(candidate, parsedAddress, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedAddress = parsedAddress.ToLowerInvariant();
        return true;
    }
}

public sealed class DpapiRemoteImageSenderTrustProtector : IRemoteImageSenderTrustProtector
{
    private static readonly byte[] OptionalEntropy = "UnifiedMessenger.RemoteImageSenderTrust.v1"u8.ToArray();

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

public sealed class FileRemoteImageSenderTrustStore : IRemoteImageSenderTrustStore, IDisposable
{
    private readonly string _trustFolder;
    private readonly IRemoteImageSenderTrustProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileRemoteImageSenderTrustStore(
        IAppPaths appPaths,
        IRemoteImageSenderTrustProtector protector)
        : this(
            appPaths?.RemoteImageSenderTrustFolder ?? throw new ArgumentNullException(nameof(appPaths)),
            protector)
    {
    }

    public FileRemoteImageSenderTrustStore(
        string trustFolder,
        IRemoteImageSenderTrustProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustFolder);
        _trustFolder = Path.GetFullPath(trustFolder);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public async Task<bool> IsTrustedAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(accountId, normalizedSenderAddress);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            HashSet<string> trustedSenders = await LoadAsync(accountId, cancellationToken);
            return trustedSenders.Contains(normalizedSenderAddress);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrustAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(accountId, normalizedSenderAddress);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            HashSet<string> trustedSenders = await LoadAsync(accountId, cancellationToken);
            if (trustedSenders.Add(normalizedSenderAddress))
            {
                await SaveAsync(accountId, trustedSenders, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RevokeAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default)
    {
        Validate(accountId, normalizedSenderAddress);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            HashSet<string> trustedSenders = await LoadAsync(accountId, cancellationToken);
            if (!trustedSenders.Remove(normalizedSenderAddress))
            {
                return;
            }

            if (trustedSenders.Count == 0)
            {
                DeleteAccountFile(accountId);
            }
            else
            {
                await SaveAsync(accountId, trustedSenders, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            DeleteAccountFile(accountId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<HashSet<string>> LoadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        string path = GetAccountPath(accountId);
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
            string[] values = JsonSerializer.Deserialize<string[]>(plaintext) ?? [];
            HashSet<string> normalizedValues = new(StringComparer.Ordinal);
            foreach (string value in values)
            {
                if (RemoteImageSenderIdentity.TryNormalize(value, out string normalized))
                {
                    normalizedValues.Add(normalized);
                }
            }

            return normalizedValues;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
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

    private async Task SaveAsync(
        Guid accountId,
        IReadOnlyCollection<string> trustedSenders,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_trustFolder);
        string targetPath = GetAccountPath(accountId);
        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            trustedSenders.OrderBy(value => value, StringComparer.Ordinal).ToArray());
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

    private void DeleteAccountFile(Guid accountId)
    {
        string path = GetAccountPath(accountId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetAccountPath(Guid accountId)
    {
        string path = Path.GetFullPath(Path.Combine(_trustFolder, accountId.ToString("N") + ".trust.bin"));
        string folderPrefix = _trustFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Remote image sender trust path escaped its storage folder.");
        }

        return path;
    }

    private static void Validate(Guid accountId, string normalizedSenderAddress)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        if (!RemoteImageSenderIdentity.TryNormalize(normalizedSenderAddress, out string normalized)
            || !string.Equals(normalizedSenderAddress, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("Sender address must be normalized.", nameof(normalizedSenderAddress));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class NullRemoteImageSenderTrustStore : IRemoteImageSenderTrustStore
{
    public static readonly NullRemoteImageSenderTrustStore Instance = new();

    public Task<bool> IsTrustedAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task TrustAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RevokeAsync(
        Guid accountId,
        string normalizedSenderAddress,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
