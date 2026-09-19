using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed record ManagedImapDraftIdentity(
    string FolderLocator,
    uint UidValidity,
    uint UniqueId,
    string LogicalId);

public sealed record ManagedImapDraftLoadResult(
    ManagedImapDraftIdentity Identity,
    MailComposeTemplate Template);

public sealed class ManagedImapDraftException(
    MailSendFailureKind failureKind,
    string userMessage,
    Exception? innerException = null) : Exception(userMessage, innerException)
{
    public MailSendFailureKind FailureKind { get; } = failureKind;
    public string UserMessage { get; } = userMessage;
}

public enum ManagedImapDraftSaveStatus
{
    Saved,
    Failed,
    Ambiguous
}

public sealed record ManagedImapDraftSaveResult(
    ManagedImapDraftSaveStatus Status,
    ManagedImapDraftIdentity? Identity,
    string LogicalId,
    MailSendFailureKind? FailureKind = null,
    string? UserMessage = null)
{
    public bool IsSaved => Status is ManagedImapDraftSaveStatus.Saved;
}

public interface IManagedImapDraftService
{
    Task<ManagedImapDraftLoadResult> LoadAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default);

    Task<ManagedImapDraftSaveResult> SaveAsync(
        MailAccount account,
        ManagedImapDraftIdentity? identity,
        string? logicalId,
        MailComposeRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        MailAccount account,
        ManagedImapDraftIdentity identity,
        CancellationToken cancellationToken = default);
}

internal sealed record ManagedImapDraftFolderState(
    string FolderLocator,
    uint UidValidity,
    bool SupportsReplace,
    bool SupportsUidPlus,
    bool CanDelete);

internal interface IManagedImapDraftSessionFactory
{
    IManagedImapDraftSession Create();
}

internal interface IManagedImapDraftSession : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(MailServerSettings server, string secret, CancellationToken cancellationToken);
    Task<ManagedImapDraftFolderState> OpenDraftsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<uint>> FindByLogicalIdAsync(string logicalId, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(uint uid, CancellationToken cancellationToken);
    Task<UniqueId?> AppendAsync(MimeMessage message, CancellationToken cancellationToken);
    Task<UniqueId?> ReplaceAsync(uint uid, MimeMessage message, CancellationToken cancellationToken);
    Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken cancellationToken);
    Task DeleteExactAsync(uint uid, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal sealed class ManagedImapDraftService(
    IMailCredentialStore credentialStore,
    IMailProviderFactory providerFactory,
    IManagedImapDraftSessionFactory sessionFactory,
    IMailOutgoingAttachmentMaterializer attachmentMaterializer,
    IMailMimeMessageFactory mimeMessageFactory) : IManagedImapDraftService
{
    internal const string LogicalIdHeader = "X-Lantern-Draft-Id";
    private const string RichDraftWarning =
        "Этот черновик содержит HTML-форматирование, которое raven не может сохранить без потерь.";

    public async Task<ManagedImapDraftLoadResult> LoadAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        if (!MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap || !account.IsEnabled)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.InvalidRequest,
                "Почтовый аккаунт недоступен.");
        }

        (uint expectedValidity, uint uid) identity;
        try
        {
            identity = ImapMailReadProvider.ParseMessageKey(MailFolderKind.Drafts, messageKey);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or MailReadException)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.InvalidRequest,
                "Идентификатор черновика некорректен.",
                exception);
        }

        (MailServerSettings server, string secret) = await ResolveConnectionAsync(account, cancellationToken);
        using IManagedImapDraftSession session = sessionFactory.Create();
        try
        {
            await session.ConnectAsync(server, secret, cancellationToken);
            ManagedImapDraftFolderState folder = await session.OpenDraftsAsync(cancellationToken);
            if (folder.UidValidity != identity.expectedValidity)
            {
                throw new ManagedImapDraftException(
                    MailSendFailureKind.InvalidRequest,
                    "Список черновиков изменился. Обновите папку и повторите попытку.");
            }
            if (!await session.ExistsAsync(identity.uid, cancellationToken))
            {
                throw new ManagedImapDraftException(
                    MailSendFailureKind.InvalidRequest,
                    "Черновик больше недоступен.");
            }

            MimeMessage message = await session.GetMessageAsync(identity.uid, cancellationToken);
            string logicalId = NormalizeLogicalId(message.Headers[LogicalIdHeader]);
            bool isReadOnly = !string.IsNullOrWhiteSpace(message.HtmlBody);
            MailComposeTemplate template = new(
                MailContentExtractor.FormatAddresses(message.To),
                MailContentExtractor.FormatAddresses(message.Cc),
                MailContentExtractor.FormatAddresses(message.Bcc),
                message.Subject?.Trim() ?? string.Empty,
                MailContentExtractor.NormalizePlainText(message.TextBody),
                CreateReplyContext(message))
            {
                ExistingAttachments = ExtractAttachments(message, cancellationToken),
                IsReadOnly = isReadOnly,
                RestrictionMessage = isReadOnly ? RichDraftWarning : null
            };
            return new(
                new ManagedImapDraftIdentity(
                    folder.FolderLocator,
                    folder.UidValidity,
                    identity.uid,
                    logicalId),
                template);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedImapDraftException)
        {
            throw;
        }
        catch (MailAttachmentException exception)
        {
            throw new ManagedImapDraftException(
                exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                    ? MailSendFailureKind.MessageTooLarge
                    : MailSendFailureKind.AttachmentUnavailable,
                exception.UserMessage,
                exception);
        }
        catch (MailKit.Security.AuthenticationException exception)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.AuthenticationFailed,
                "Не удалось войти в почтовый аккаунт. Проверьте пароль приложения.",
                exception);
        }
        catch (Exception exception) when (exception is FolderNotFoundException or NotSupportedException)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.CapabilityUnavailable,
                "Сервер не предоставил системную папку «Черновики».",
                exception);
        }
        catch (Exception exception) when (IsTransportFailure(exception) || exception is CommandException)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.ConnectionFailed,
                "Не удалось открыть черновик. Проверьте подключение.",
                exception);
        }
        finally
        {
            await DisconnectQuietlyAsync(session);
        }
    }

    public async Task<ManagedImapDraftSaveResult> SaveAsync(
        MailAccount account,
        ManagedImapDraftIdentity? identity,
        string? logicalId,
        MailComposeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);
        string token = string.IsNullOrWhiteSpace(logicalId)
            ? Guid.NewGuid().ToString("N")
            : logicalId.Trim();
        if (!Guid.TryParseExact(token, "N", out _)
            || identity is not null && !string.Equals(identity.LogicalId, token, StringComparison.Ordinal))
        {
            return Failed(token, MailSendFailureKind.InvalidRequest, "Идентификатор черновика некорректен.");
        }
        if (!MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap
            || !account.IsEnabled
            || request.AccountId != account.Id)
        {
            return Failed(token, MailSendFailureKind.InvalidRequest, "Параметры черновика некорректны.");
        }

        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
        {
            return Failed(token, MailSendFailureKind.AuthenticationFailed, "Не удалось войти в почтовый аккаунт. Проверьте пароль приложения.");
        }

        if (providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            return Failed(token, MailSendFailureKind.CapabilityUnavailable, "Черновики для этого аккаунта недоступны.");
        }

        MailConnectionSettings? settings = provider.CreateConnectionSettings(
            new(account.Provider, account.EmailAddress, account.DisplayName, account.GenericConnectionSettings),
            account.EmailAddress.Trim());
        if (settings is null)
        {
            return Failed(token, MailSendFailureKind.CapabilityUnavailable, "Черновики для этого аккаунта недоступны.");
        }

        MimeMessage message;
        try
        {
            IReadOnlyList<MaterializedMailAttachment> attachments = request.Attachments.Count == 0
                ? []
                : await attachmentMaterializer.MaterializeAsync(account, request.Attachments, cancellationToken);
            message = mimeMessageFactory.CreateDraft(account, request, attachments).Message;
            message.Headers[LogicalIdHeader] = token;
        }
        catch (MailAttachmentException exception)
        {
            return Failed(
                token,
                exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                    ? MailSendFailureKind.MessageTooLarge
                    : MailSendFailureKind.AttachmentUnavailable,
                exception.UserMessage);
        }
        catch (MailComposeValidationException exception)
        {
            return Failed(token, MailSendFailureKind.InvalidRequest, exception.UserMessage);
        }

        using IManagedImapDraftSession session = sessionFactory.Create();
        bool mutationAttempted = false;
        ManagedImapDraftIdentity? latestConfirmedIdentity = identity;
        try
        {
            await session.ConnectAsync(settings.Imap, credential.Secret, cancellationToken);
            ManagedImapDraftFolderState folder = await session.OpenDraftsAsync(cancellationToken);
            ManagedImapDraftIdentity? current = identity is not null
                && string.Equals(identity.FolderLocator, folder.FolderLocator, StringComparison.Ordinal)
                && identity.UidValidity == folder.UidValidity
                    ? identity
                    : null;
            latestConfirmedIdentity = current;

            IReadOnlyList<uint> prior = await session.FindByLogicalIdAsync(token, cancellationToken);
            if (current is not null && !prior.Contains(current.UniqueId)
                && await session.ExistsAsync(current.UniqueId, cancellationToken))
            {
                prior = [.. prior, current.UniqueId];
            }
            else if (current is not null && !prior.Contains(current.UniqueId) && prior.Count == 1)
            {
                // A previous REPLACE may have completed while its response was lost.
                // The opaque per-compose header safely identifies the replacement.
                current = new(folder.FolderLocator, folder.UidValidity, prior[0], token);
                latestConfirmedIdentity = current;
            }

            // RFC 8508 REPLACE is atomic. Prefer it only for one confirmed current UID;
            // otherwise fall back to append-first replacement so no valid draft is lost.
            if (current is not null && folder.SupportsReplace && prior.Count <= 1)
            {
                mutationAttempted = true;
                UniqueId? replacement = await session.ReplaceAsync(current.UniqueId, message, cancellationToken);
                uint? replacementUid = ValidUid(replacement, folder.UidValidity);
                if (replacementUid is null)
                {
                    IReadOnlyList<uint> after = await session.FindByLogicalIdAsync(token, cancellationToken);
                    replacementUid = after.Count == 1 && after[0] != current.UniqueId
                        ? after[0]
                        : null;
                }

                if (replacementUid is uint replaced)
                {
                    latestConfirmedIdentity = new(folder.FolderLocator, folder.UidValidity, replaced, token);
                }
                return replacementUid is uint replacedUid
                    ? Saved(folder, replacedUid, token)
                    : Ambiguous(current, token);
            }

            if (prior.Count > 0 && (!folder.SupportsUidPlus || !folder.CanDelete))
            {
                return Failed(
                    token,
                    MailSendFailureKind.CapabilityUnavailable,
                    "Сервер не поддерживает безопасное обновление черновика.");
            }

            mutationAttempted = true;
            UniqueId? appended = await session.AppendAsync(message, cancellationToken);
            uint? newUid = ValidUid(appended, folder.UidValidity);
            if (newUid is null)
            {
                IReadOnlyList<uint> after = await session.FindByLogicalIdAsync(token, cancellationToken);
                uint[] added = after.Except(prior).ToArray();
                newUid = added.Length == 1 ? added[0] : null;
            }

            if (newUid is not uint confirmedUid)
            {
                return Ambiguous(current, token);
            }

            latestConfirmedIdentity = new(folder.FolderLocator, folder.UidValidity, confirmedUid, token);

            // APPEND is confirmed before any prior UID is deleted. UID EXPUNGE is
            // exact, so unrelated messages already carrying \Deleted are untouched.
            foreach (uint oldUid in prior.Where(uid => uid != confirmedUid).Distinct())
            {
                await session.DeleteExactAsync(oldUid, cancellationToken);
            }

            return Saved(folder, confirmedUid, token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return mutationAttempted
                ? Ambiguous(latestConfirmedIdentity, token)
                : Failed(token, MailSendFailureKind.CanceledBeforeSubmission, "Сохранение черновика отменено.");
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return Failed(token, MailSendFailureKind.AuthenticationFailed, "Не удалось войти в почтовый аккаунт. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (exception is FolderNotFoundException or NotSupportedException)
        {
            return Failed(token, MailSendFailureKind.CapabilityUnavailable, "Сервер не предоставил системную папку «Черновики».");
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            return mutationAttempted
                ? Ambiguous(latestConfirmedIdentity, token)
                : Failed(token, MailSendFailureKind.ConnectionFailed, "Не удалось сохранить черновик. Проверьте подключение.");
        }
        catch (CommandException)
        {
            return mutationAttempted
                ? Ambiguous(latestConfirmedIdentity, token)
                : Failed(token, MailSendFailureKind.ProtocolRejected, "Сервер отклонил сохранение черновика.");
        }
        finally
        {
            await DisconnectQuietlyAsync(session);
        }
    }

    public async Task DeleteAsync(
        MailAccount account,
        ManagedImapDraftIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(identity);
        if (!MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap || !account.IsEnabled)
        {
            throw new InvalidOperationException("Managed IMAP draft account is unavailable.");
        }

        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid()
            || providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            throw new InvalidOperationException("Managed IMAP draft credentials are unavailable.");
        }

        MailConnectionSettings settings = provider.CreateConnectionSettings(
            new(account.Provider, account.EmailAddress, account.DisplayName, account.GenericConnectionSettings),
            account.EmailAddress.Trim()) ?? throw new InvalidOperationException("Managed IMAP is unavailable.");
        using IManagedImapDraftSession session = sessionFactory.Create();
        try
        {
            await session.ConnectAsync(settings.Imap, credential.Secret, cancellationToken);
            ManagedImapDraftFolderState folder = await session.OpenDraftsAsync(cancellationToken);
            if (!folder.SupportsUidPlus
                || !folder.CanDelete
                || folder.UidValidity != identity.UidValidity
                || !string.Equals(folder.FolderLocator, identity.FolderLocator, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Managed IMAP draft identity is stale or exact deletion is unavailable.");
            }

            if (await session.ExistsAsync(identity.UniqueId, cancellationToken))
            {
                await session.DeleteExactAsync(identity.UniqueId, cancellationToken);
            }
        }
        finally
        {
            await DisconnectQuietlyAsync(session);
        }
    }

    private async Task<(MailServerSettings Server, string Secret)> ResolveConnectionAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        MailCredential? credential;
        try
        {
            credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.CredentialMissing,
                "Не удалось прочитать защищённые данные почтового аккаунта.",
                exception);
        }

        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.AuthenticationFailed,
                "Не удалось войти в почтовый аккаунт. Проверьте пароль приложения.");
        }

        if (providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            throw new ManagedImapDraftException(
                MailSendFailureKind.CapabilityUnavailable,
                "Черновики для этого аккаунта недоступны.");
        }

        MailConnectionSettings settings = provider.CreateConnectionSettings(
            new(account.Provider, account.EmailAddress, account.DisplayName, account.GenericConnectionSettings),
            account.EmailAddress.Trim()) ?? throw new ManagedImapDraftException(
                MailSendFailureKind.CapabilityUnavailable,
                "Черновики для этого аккаунта недоступны.");
        return (settings.Imap, credential.Secret);
    }

    private static string NormalizeLogicalId(string? value) =>
        Guid.TryParseExact(value?.Trim(), "N", out Guid parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");

    private static IReadOnlyList<OutgoingMailAttachment> ExtractAttachments(
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        List<OutgoingMailAttachment> result = [];
        foreach (MailAttachmentInfo attachment in MailMimeAttachmentCatalog.Extract(message)
                     .Where(item => item.IsDownloadable))
        {
            MailAttachmentContent content = MailMimeAttachmentCatalog.GetContent(
                message,
                attachment.AttachmentKey,
                cancellationToken);
            result.Add(OutgoingMailAttachment.FromMemory(content));
        }
        return result;
    }

    private static MailReplyContext? CreateReplyContext(MimeMessage message)
    {
        string? inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo;
        string[] references = message.References
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return inReplyTo is null && references.Length == 0
            ? null
            : new MailReplyContext(inReplyTo, references);
    }

    private static uint? ValidUid(UniqueId? uid, uint expectedValidity) =>
        uid is { IsValid: true } value
        && (value.Validity == 0 || value.Validity == expectedValidity)
            ? value.Id
            : null;

    private static ManagedImapDraftSaveResult Saved(ManagedImapDraftFolderState folder, uint uid, string token) =>
        new(
            ManagedImapDraftSaveStatus.Saved,
            new ManagedImapDraftIdentity(folder.FolderLocator, folder.UidValidity, uid, token),
            token);

    private static ManagedImapDraftSaveResult Failed(
        string token,
        MailSendFailureKind failureKind,
        string userMessage) =>
        new(ManagedImapDraftSaveStatus.Failed, null, token, failureKind, userMessage);

    private static ManagedImapDraftSaveResult Ambiguous(ManagedImapDraftIdentity? identity, string token) =>
        new(
            ManagedImapDraftSaveStatus.Ambiguous,
            identity,
            token,
            MailSendFailureKind.Ambiguous,
            "raven не может подтвердить состояние черновика. Проверьте папку «Черновики» перед повтором.");

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or ProtocolException
            or ServiceNotConnectedException
            or ServiceNotAuthenticatedException;

    private static async Task DisconnectQuietlyAsync(IManagedImapDraftSession session)
    {
        if (!session.IsConnected)
        {
            return;
        }

        try
        {
            await session.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception) when (IsTransportFailure(exception) || exception is CommandException)
        {
        }
    }
}

internal sealed class MailKitManagedImapDraftSessionFactory : IManagedImapDraftSessionFactory
{
    public IManagedImapDraftSession Create() => new MailKitManagedImapDraftSession();
}

internal sealed class MailKitManagedImapDraftSession : IManagedImapDraftSession
{
    private readonly ImapClient _client = new();
    private IMailFolder? _drafts;

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(
            server.Host,
            server.Port,
            server.SecureSocketMode switch
            {
                MailSecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
                MailSecureSocketMode.StartTls => SecureSocketOptions.StartTls,
                _ => throw new NotSupportedException("Secure IMAP transport is required.")
            },
            cancellationToken);
        await _client.AuthenticateAsync(server.Username, secret, cancellationToken);
    }

    public async Task<ManagedImapDraftFolderState> OpenDraftsAsync(CancellationToken cancellationToken)
    {
        _drafts = _client.GetFolder(SpecialFolder.Drafts);
        if (_drafts is not { Exists: true }
            || _drafts.Attributes.HasFlag(FolderAttributes.NoSelect)
            || await _drafts.OpenAsync(FolderAccess.ReadWrite, cancellationToken) != FolderAccess.ReadWrite)
        {
            throw new NotSupportedException("A selectable Drafts folder is required.");
        }

        return new(
            _drafts.FullName,
            _drafts.UidValidity,
            _client.Capabilities.HasFlag(ImapCapabilities.Replace),
            _client.Capabilities.HasFlag(ImapCapabilities.UidPlus),
            _drafts.PermanentFlags.HasFlag(MessageFlags.Deleted));
    }

    public async Task<IReadOnlyList<uint>> FindByLogicalIdAsync(
        string logicalId,
        CancellationToken cancellationToken)
    {
        // Mail.ru advertises XLIST/UIDPLUS but rejects SEARCH HEADER for
        // arbitrary X-* fields. Search only the Drafts mailbox, then fetch the
        // single reconciliation header without downloading message bodies.
        IList<UniqueId> candidates = await _drafts!.SearchAsync(
            SearchQuery.All,
            cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        FetchRequest request = new(
            MessageSummaryItems.UniqueId | MessageSummaryItems.Headers,
            [ManagedImapDraftService.LogicalIdHeader]);
        IList<IMessageSummary> summaries = await _drafts.FetchAsync(
            candidates,
            request,
            cancellationToken);
        return summaries
            .Where(summary => summary.UniqueId.IsValid
                && string.Equals(
                    summary.Headers?[ManagedImapDraftService.LogicalIdHeader],
                    logicalId,
                    StringComparison.Ordinal))
            .Select(summary => summary.UniqueId.Id)
            .Distinct()
            .ToArray();
    }

    public async Task<bool> ExistsAsync(uint uid, CancellationToken cancellationToken) =>
        (await _drafts!.FetchAsync([new UniqueId(uid)], MessageSummaryItems.UniqueId, cancellationToken))
            .Any(summary => summary.UniqueId.Id == uid);

    public Task<UniqueId?> AppendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        _drafts!.AppendAsync(new AppendRequest(message, MessageFlags.Draft | MessageFlags.Seen), cancellationToken);

    public Task<UniqueId?> ReplaceAsync(uint uid, MimeMessage message, CancellationToken cancellationToken) =>
        _drafts!.ReplaceAsync(
            new UniqueId(uid),
            new ReplaceRequest(message, MessageFlags.Draft | MessageFlags.Seen),
            cancellationToken);

    public Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken cancellationToken) =>
        _drafts!.GetMessageAsync(new UniqueId(uid), cancellationToken);

    public async Task DeleteExactAsync(uint uid, CancellationToken cancellationToken)
    {
        UniqueId uniqueId = new(uid);
        await _drafts!.AddFlagsAsync([uniqueId], MessageFlags.Deleted, silent: true, cancellationToken);
        await _drafts!.ExpungeAsync([uniqueId], cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(true, cancellationToken);

    // Dispose without CLOSE, so unrelated messages marked \Deleted are never expunged.
    public void Dispose() => _client.Dispose();
}
