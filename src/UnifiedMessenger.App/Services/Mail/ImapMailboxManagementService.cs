using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using UnifiedMessenger.App.Models;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed class ImapMailboxManagementService(
    IMailCredentialStore credentials,
    IMailProviderFactory providers,
    IImapMailboxSessionFactory sessions,
    ImapMailboxChangeTracker changes) : IMailMailboxManagementService
{
    public bool Supports(MailProviderType provider) =>
        MailProviderFeaturePolicies.Get(provider).IsManagedImap;

    public bool CanApply(MailFolderKind source, MailMailboxAction action) => action switch
    {
        MailMailboxAction.Archive or MailMailboxAction.Spam => source is MailFolderKind.Inbox,
        MailMailboxAction.Trash => source is MailFolderKind.Inbox or MailFolderKind.Sent
            or MailFolderKind.Drafts or MailFolderKind.Spam or MailFolderKind.Archive,
        MailMailboxAction.NotSpam => source is MailFolderKind.Spam,
        MailMailboxAction.Restore => source is MailFolderKind.Trash,
        MailMailboxAction.Read or MailMailboxAction.Unread =>
            source is MailFolderKind.Inbox or MailFolderKind.Sent or MailFolderKind.Spam
                or MailFolderKind.Trash or MailFolderKind.Archive,
        _ => false
    };

    internal static MailFolderKind? Destination(MailMailboxAction action) => action switch
    {
        MailMailboxAction.Archive => MailFolderKind.Archive,
        MailMailboxAction.Trash => MailFolderKind.Trash,
        MailMailboxAction.Spam => MailFolderKind.Spam,
        MailMailboxAction.NotSpam or MailMailboxAction.Restore => MailFolderKind.Inbox,
        _ => null
    };

    public async Task<MailMailboxMutationResult> ApplyAsync(
        MailAccount account, MailFolder source, IReadOnlyCollection<string> messageKeys,
        MailMailboxAction action, CancellationToken cancellationToken = default)
    {
        string[] keys = messageKeys.Distinct(StringComparer.Ordinal).ToArray();
        MailFolderKind? destination = Destination(action);
        List<string> succeeded = [];
        List<string> failed = [];
        List<MailMailboxMutationItemResult> itemResults = [];
        bool attempted = false;
        bool inboxMoveUncertain = false;
        int activeIndex = -1;
        bool activeMutationStarted = false;
        string? activeDestinationMessageKey = null;
        if (!Supports(account.Provider) || !account.IsEnabled || !CanApply(source.Kind, action)
            || keys.Length is 0 or > 1000)
        {
            return Failure(keys, destination, "Действие недоступно для этой папки.");
        }

        using IDisposable lease = await changes.EnterAsync(account.Id, cancellationToken);
        try
        {
            var identities = keys.Select(key => (Key: key,
                Identity: ImapMailReadProvider.ParseMessageKey(source.Kind, key))).ToArray();
            uint validity = identities[0].Identity.UidValidity;
            if (validity == 0 || identities.Any(item => item.Identity.UidValidity != validity))
            {
                return Failure(keys, destination,
                    "Список папки изменился. Обновите её и повторите действие.", requiresRefresh: true);
            }

            MailCredential? credential = await credentials.LoadAsync(account.CredentialKey, cancellationToken);
            if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
            {
                return Failure(keys, destination, "Не удалось войти в почту. Проверьте пароль приложения.");
            }

            if (providers.Get(account.Provider) is not PasswordMailProvider provider)
            {
                return Failure(keys, destination, "Почтовый аккаунт настроен некорректно.");
            }

            MailServerSettings server = provider.CreateConnectionSettings(
                new(account.Provider, account.EmailAddress, account.DisplayName, account.GenericConnectionSettings),
                account.EmailAddress.Trim())!.Imap;
            using IImapMailboxSession session = sessions.Create();
            await session.ConnectAsync(server, credential.Secret, cancellationToken);
            if (destination is MailFolderKind target)
            {
                await session.ResolveDestinationAsync(target, cancellationToken);
            }
            if (destination is not null && !session.SupportsMove && !session.SupportsUidPlus)
            {
                return Failure(keys, destination, "Сервер не поддерживает безопасное перемещение этих писем.");
            }

            ImapMailboxSourceState opened = await session.OpenSourceAsync(source, cancellationToken);
            if (opened.UidValidity != validity)
            {
                return Failure(keys, destination,
                    "Список папки изменился. Обновите её и повторите действие.", requiresRefresh: true);
            }

            if (destination is null && !opened.PermanentFlags.HasFlag(MessageFlags.Seen))
            {
                return Failure(keys, destination, "Сервер не разрешил изменить эту пометку.");
            }
            if (destination is not null && !session.SupportsMove && !opened.PermanentFlags.HasFlag(MessageFlags.Deleted))
            {
                return Failure(keys, destination, "Сервер не разрешил безопасное перемещение.");
            }

            // One UID at a time gives an exact partial-success contract and prevents
            // a missing/stale UID from being silently reported as a successful batch.
            for (int index = 0; index < identities.Length; index++)
            {
                var item = identities[index];
                activeIndex = index;
                activeMutationStarted = false;
                activeDestinationMessageKey = null;
                cancellationToken.ThrowIfCancellationRequested();
                uint uid = item.Identity.UniqueId;
                if (!await session.ExistsAsync(uid, cancellationToken))
                {
                    failed.Add(item.Key);
                    itemResults.Add(new(item.Key, MailMailboxMutationItemStatus.Failed));
                    activeIndex = -1;
                    continue;
                }

                attempted = true;
                activeMutationStarted = true;
                string? destinationMessageKey = null;
                if (destination is not null)
                {
                    inboxMoveUncertain = destination is MailFolderKind.Inbox;
                    UniqueId? destinationUid;
                    if (session.SupportsMove)
                    {
                        destinationUid = await session.MoveAsync(uid, cancellationToken);
                    }
                    else
                    {
                        destinationUid = await session.CopyAsync(uid, cancellationToken);
                        if (destinationUid is not { IsValid: true, Validity: > 0 })
                        {
                            throw new InvalidOperationException("Missing UIDPLUS mapping.");
                        }
                        destinationMessageKey = ImapMailReadProvider.CreateMessageKey(
                            destination.Value, destinationUid.Value.Validity, destinationUid.Value.Id);
                        activeDestinationMessageKey = destinationMessageKey;
                        if (destination is MailFolderKind.Inbox)
                        {
                            changes.RecordInboxMove(
                                account.Id, destinationUid.Value.Validity, [destinationUid.Value.Id]);
                            inboxMoveUncertain = false;
                        }
                        await session.SetFlagAsync(uid, MessageFlags.Deleted, true, cancellationToken);
                        await session.ExpungeUidAsync(uid, cancellationToken);
                    }

                    if (destinationMessageKey is null
                        && destinationUid is { IsValid: true, Validity: > 0 } mappedUid)
                    {
                        destinationMessageKey = ImapMailReadProvider.CreateMessageKey(
                            destination.Value, mappedUid.Validity, mappedUid.Id);
                        activeDestinationMessageKey = destinationMessageKey;
                        if (destination is MailFolderKind.Inbox)
                        {
                            changes.RecordInboxMove(account.Id, mappedUid.Validity, [mappedUid.Id]);
                        }
                        inboxMoveUncertain = false;
                    }
                    else if (session.SupportsMove)
                    {
                        // MOVE completed successfully, but the server omitted COPYUID.
                        // Rebaseline polling because the destination UID cannot be suppressed directly.
                        if (destination is MailFolderKind.Inbox)
                        {
                            changes.RequireBaseline(account.Id);
                        }
                        inboxMoveUncertain = false;
                    }
                }
                else
                {
                    await session.SetFlagAsync(uid, MessageFlags.Seen,
                        action is MailMailboxAction.Read, cancellationToken);
                }
                succeeded.Add(item.Key);
                itemResults.Add(new(
                    item.Key,
                    MailMailboxMutationItemStatus.Succeeded,
                    destinationMessageKey));
                activeMutationStarted = false;
                activeDestinationMessageKey = null;
                activeIndex = -1;
            }

            return new(succeeded, failed, destination,
                failed.Count > 0 ? "Некоторые письма уже отсутствуют. Список обновлён." : null,
                true,
                itemResults);
        }
        catch (Exception exception)
        {
            // Never retry COPY/MOVE after an ambiguous network failure. A copy may
            // already exist; refresh both folders before the user decides what next.
            if (inboxMoveUncertain)
            {
                changes.RequireBaseline(account.Id);
            }
            if (activeIndex >= 0)
            {
                string activeKey = keys[activeIndex];
                itemResults.Add(new(
                    activeKey,
                    activeMutationStarted
                        ? MailMailboxMutationItemStatus.Ambiguous
                        : MailMailboxMutationItemStatus.NotAttempted,
                    activeDestinationMessageKey));
                for (int index = activeIndex + 1; index < keys.Length; index++)
                {
                    itemResults.Add(new(keys[index], MailMailboxMutationItemStatus.NotAttempted));
                }
            }
            else if (itemResults.Count == 0)
            {
                itemResults.AddRange(keys.Select(key =>
                    new MailMailboxMutationItemResult(key, MailMailboxMutationItemStatus.NotAttempted)));
            }
            string message = exception is MailKit.Security.AuthenticationException
                ? "Не удалось войти в почту. Проверьте пароль приложения."
                : exception is FolderNotFoundException or NotSupportedException
                    ? "Сервер не предоставил нужную системную папку или возможность."
                    : attempted
                        ? "Не удалось подтвердить все изменения. Проверьте исходную и целевую папки перед повтором."
                        : "Не удалось изменить письма. Проверьте подключение и доступ к папке.";
            return new(
                succeeded,
                keys.Except(succeeded, StringComparer.Ordinal).ToArray(),
                destination,
                message,
                attempted,
                itemResults);
        }
    }

    private static MailMailboxMutationResult Failure(
        IReadOnlyList<string> keys,
        MailFolderKind? destination,
        string message,
        bool requiresRefresh = false) =>
        new(
            [],
            keys,
            destination,
            message,
            requiresRefresh,
            keys.Select(key => new MailMailboxMutationItemResult(
                key,
                MailMailboxMutationItemStatus.Failed)).ToArray());
}

internal sealed record ImapMailboxSourceState(uint UidValidity, MessageFlags PermanentFlags);

internal interface IImapMailboxSessionFactory
{
    IImapMailboxSession Create();
}

internal interface IImapMailboxSession : IDisposable
{
    bool SupportsMove { get; }
    bool SupportsUidPlus { get; }
    Task ConnectAsync(MailServerSettings server, string secret, CancellationToken token);
    Task ResolveDestinationAsync(MailFolderKind kind, CancellationToken token);
    Task<ImapMailboxSourceState> OpenSourceAsync(MailFolder source, CancellationToken token);
    Task<bool> ExistsAsync(uint uid, CancellationToken token);
    Task<UniqueId?> MoveAsync(uint uid, CancellationToken token);
    Task<UniqueId?> CopyAsync(uint uid, CancellationToken token);
    Task SetFlagAsync(uint uid, MessageFlags flag, bool add, CancellationToken token);
    Task ExpungeUidAsync(uint uid, CancellationToken token);
}

internal sealed class MailKitMailboxSessionFactory : IImapMailboxSessionFactory
{
    public IImapMailboxSession Create() => new MailKitMailboxSession();
}

internal sealed class MailKitMailboxSession : IImapMailboxSession
{
    private readonly ImapClient _client = new();
    private IMailFolder? _source;
    private IMailFolder? _destination;
    public bool SupportsMove => _client.Capabilities.HasFlag(ImapCapabilities.Move);
    public bool SupportsUidPlus => _client.Capabilities.HasFlag(ImapCapabilities.UidPlus);

    public async Task ConnectAsync(MailServerSettings server, string secret, CancellationToken token)
    {
        await _client.ConnectAsync(server.Host, server.Port, server.SecureSocketMode switch
        {
            MailSecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
            MailSecureSocketMode.StartTls => SecureSocketOptions.StartTls,
            _ => throw new NotSupportedException()
        }, token);
        await _client.AuthenticateAsync(server.Username, secret, token);
    }

    private IMailFolder Resolve(MailFolderKind kind)
    {
        IMailFolder? folder = kind switch
        {
            MailFolderKind.Inbox => _client.Inbox,
            MailFolderKind.Sent => _client.GetFolder(SpecialFolder.Sent),
            MailFolderKind.Drafts => _client.GetFolder(SpecialFolder.Drafts),
            MailFolderKind.Spam => _client.GetFolder(SpecialFolder.Junk),
            MailFolderKind.Trash => _client.GetFolder(SpecialFolder.Trash),
            MailFolderKind.Archive => _client.GetFolder(SpecialFolder.Archive),
            _ => null
        };
        if (folder is not { Exists: true } || folder.Attributes.HasFlag(FolderAttributes.NoSelect)
            || kind is MailFolderKind.Archive && MailKitImapInboxClient.DescribeArchiveFolder(
                folder.FullName, _client.Inbox.FullName, folder.Attributes, _client.Capabilities) is null)
        {
            throw new NotSupportedException();
        }
        return folder;
    }

    public Task ResolveDestinationAsync(MailFolderKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _destination = Resolve(kind);
        return Task.CompletedTask;
    }

    public async Task<ImapMailboxSourceState> OpenSourceAsync(MailFolder source, CancellationToken token)
    {
        _source = Resolve(source.Kind);
        if (!string.Equals(_source.FullName, source.ProviderLocator, StringComparison.Ordinal)
            || _destination is not null && string.Equals(_source.FullName, _destination.FullName, StringComparison.Ordinal))
        {
            throw new NotSupportedException();
        }
        if (await _source.OpenAsync(FolderAccess.ReadWrite, token) != FolderAccess.ReadWrite)
        {
            throw new NotSupportedException();
        }
        return new(_source.UidValidity, _source.PermanentFlags);
    }

    public async Task<bool> ExistsAsync(uint uid, CancellationToken token) =>
        (await _source!.FetchAsync([new UniqueId(uid)], MessageSummaryItems.UniqueId, token))
            .Any(item => item.UniqueId.Id == uid);

    public Task<UniqueId?> MoveAsync(uint uid, CancellationToken token) =>
        _source!.MoveToAsync(new UniqueId(uid), _destination!, token);

    public Task<UniqueId?> CopyAsync(uint uid, CancellationToken token) =>
        _source!.CopyToAsync(new UniqueId(uid), _destination!, token);

    public Task SetFlagAsync(uint uid, MessageFlags flag, bool add, CancellationToken token) => add
        ? _source!.AddFlagsAsync([new UniqueId(uid)], flag, silent: true, token)
        : _source!.RemoveFlagsAsync([new UniqueId(uid)], flag, silent: true, token);

    public Task ExpungeUidAsync(uint uid, CancellationToken token) =>
        _source!.ExpungeAsync([new UniqueId(uid)], token);

    // Dispose the transport without CLOSE/EXPUNGE; unrelated \Deleted mail is untouched.
    public void Dispose() => _client.Dispose();
}
