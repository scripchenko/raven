using System.IO;
using System.Net;
using System.Net.Http;
using Google;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public enum GmailMailboxFailureKind
{
    ReauthorizationRequired,
    NotAuthorized,
    TransientFailure,
    PermanentFailure
}

public sealed record GmailUserLabel(string Id, string DisplayName);

public sealed record GmailMailboxItemFailure(
    string MessageKey,
    GmailMailboxFailureKind FailureKind);

public sealed record GmailMailboxMutationResult(
    IReadOnlyList<string> SucceededMessageKeys,
    IReadOnlyList<GmailMailboxItemFailure> FailedMessages)
{
    public bool IsSuccess => FailedMessages.Count == 0;
    public bool IsPartialSuccess => SucceededMessageKeys.Count > 0 && FailedMessages.Count > 0;
    public GmailMailboxFailureKind? FailureKind => FailedMessages.FirstOrDefault()?.FailureKind;
}

public sealed record GmailUserLabelResult(
    IReadOnlyList<GmailUserLabel> Labels,
    GmailMailboxFailureKind? FailureKind = null)
{
    public bool IsSuccess => FailureKind is null;
}

public interface IGmailMailboxManagementService
{
    Task<GmailUserLabelResult> GetUserLabelsAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> SetStarredAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        bool isStarred,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> SetReadStateAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        bool isRead,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> ArchiveAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> MoveToTrashAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> RestoreFromTrashAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> MarkNotSpamAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> ReportSpamAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default);

    Task<GmailMailboxMutationResult> SetUserLabelAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        string labelId,
        bool isApplied,
        CancellationToken cancellationToken = default);

    void RemoveAccount(Guid accountId);
}

internal sealed class GmailMailboxManagementService(
    IMailCredentialStore credentialStore,
    IGmailMailboxApiClient apiClient) : IGmailMailboxManagementService
{
    internal const int MaximumBatchSize = 1000;
    private readonly Dictionary<Guid, IReadOnlyList<GmailUserLabel>> _labelCache = [];

    public async Task<GmailUserLabelResult> GetUserLabelsAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsGmail(account))
        {
            return new GmailUserLabelResult([], GmailMailboxFailureKind.PermanentFailure);
        }

        if (!forceRefresh && _labelCache.TryGetValue(account.Id, out IReadOnlyList<GmailUserLabel>? cached))
        {
            return new GmailUserLabelResult(cached);
        }

        try
        {
            MailCredential credential = await LoadModifyCredentialAsync(account, cancellationToken);
            IReadOnlyList<GmailApiUserLabel> labels = await apiClient.GetUserLabelsAsync(
                credential,
                account.Id,
                cancellationToken);
            GmailUserLabel[] safeLabels = labels
                .Where(label => !string.IsNullOrWhiteSpace(label.Id)
                    && !string.IsNullOrWhiteSpace(label.Name))
                .Select(label => new GmailUserLabel(label.Id, label.Name))
                .ToArray();
            _labelCache[account.Id] = safeLabels;
            return new GmailUserLabelResult(safeLabels);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new GmailUserLabelResult([], Classify(exception));
        }
    }

    public Task<GmailMailboxMutationResult> SetStarredAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        bool isStarred,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            messageKeys,
            isStarred ? [GmailSystemFolders.Starred] : [],
            isStarred ? [] : [GmailSystemFolders.Starred],
            cancellationToken);

    public Task<GmailMailboxMutationResult> SetReadStateAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        bool isRead,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            messageKeys,
            isRead ? [] : [GmailSystemFolders.Unread],
            isRead ? [GmailSystemFolders.Unread] : [],
            cancellationToken);

    public Task<GmailMailboxMutationResult> ArchiveAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(account, messageKeys, [], [GmailSystemFolders.Inbox], cancellationToken);

    public async Task<GmailMailboxMutationResult> MoveToTrashAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default)
    {
        string[] distinctKeys = NormalizeMessageKeys(messageKeys);
        if (distinctKeys.Length == 0)
        {
            return new GmailMailboxMutationResult([], []);
        }

        try
        {
            MailCredential credential = await LoadModifyCredentialAsync(account, cancellationToken);
            Dictionary<string, string> rawToMessageKey = distinctKeys.ToDictionary(
                GmailMailReadProvider.ParseMessageKey,
                key => key,
                StringComparer.Ordinal);
            GmailApiTrashResult result = await apiClient.MoveToTrashAsync(
                credential,
                account.Id,
                rawToMessageKey.Keys.ToArray(),
                cancellationToken);
            return new GmailMailboxMutationResult(
                result.SucceededMessageIds.Select(id => rawToMessageKey[id]).ToArray(),
                result.FailedMessages.Select(item => new GmailMailboxItemFailure(
                    rawToMessageKey[item.Key],
                    Classify(item.Value))).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            GmailMailboxFailureKind failureKind = Classify(exception);
            return Failure(distinctKeys, failureKind);
        }
    }

    public async Task<GmailMailboxMutationResult> RestoreFromTrashAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default)
    {
        string[] distinctKeys = NormalizeMessageKeys(messageKeys);
        if (distinctKeys.Length == 0)
        {
            return new GmailMailboxMutationResult([], []);
        }

        try
        {
            MailCredential credential = await LoadModifyCredentialAsync(account, cancellationToken);
            Dictionary<string, string> rawToMessageKey = distinctKeys.ToDictionary(
                GmailMailReadProvider.ParseMessageKey,
                key => key,
                StringComparer.Ordinal);
            GmailApiUntrashResult result = await apiClient.RestoreFromTrashAsync(
                credential,
                account.Id,
                rawToMessageKey.Keys.ToArray(),
                cancellationToken);
            return new GmailMailboxMutationResult(
                result.SucceededMessageIds.Select(id => rawToMessageKey[id]).ToArray(),
                result.FailedMessages.Select(item => new GmailMailboxItemFailure(
                    rawToMessageKey[item.Key],
                    Classify(item.Value))).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(distinctKeys, Classify(exception));
        }
    }

    public Task<GmailMailboxMutationResult> MarkNotSpamAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            messageKeys,
            [GmailSystemFolders.Inbox],
            [GmailSystemFolders.Spam],
            cancellationToken);

    public Task<GmailMailboxMutationResult> ReportSpamAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            messageKeys,
            [GmailSystemFolders.Spam],
            [GmailSystemFolders.Inbox],
            cancellationToken);

    public async Task<GmailMailboxMutationResult> SetUserLabelAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        string labelId,
        bool isApplied,
        CancellationToken cancellationToken = default)
    {
        GmailUserLabelResult labels = await GetUserLabelsAsync(account, cancellationToken: cancellationToken);
        if (!labels.IsSuccess)
        {
            return Failure(NormalizeMessageKeys(messageKeys), labels.FailureKind!.Value);
        }

        if (!labels.Labels.Any(label => string.Equals(label.Id, labelId, StringComparison.Ordinal)))
        {
            return Failure(NormalizeMessageKeys(messageKeys), GmailMailboxFailureKind.PermanentFailure);
        }

        return await ModifyLabelsAsync(
            account,
            messageKeys,
            isApplied ? [labelId] : [],
            isApplied ? [] : [labelId],
            cancellationToken);
    }

    public void RemoveAccount(Guid accountId) => _labelCache.Remove(accountId);

    private async Task<GmailMailboxMutationResult> ModifyLabelsAsync(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys,
        IReadOnlyCollection<string> addLabelIds,
        IReadOnlyCollection<string> removeLabelIds,
        CancellationToken cancellationToken)
    {
        string[] distinctKeys = NormalizeMessageKeys(messageKeys);
        if (distinctKeys.Length == 0)
        {
            return new GmailMailboxMutationResult([], []);
        }

        List<string> succeeded = [];
        List<GmailMailboxItemFailure> failed = [];
        try
        {
            MailCredential credential = await LoadModifyCredentialAsync(account, cancellationToken);
            foreach (string[] batch in distinctKeys.Chunk(MaximumBatchSize))
            {
                try
                {
                    await apiClient.ModifyLabelsAsync(
                        credential,
                        account.Id,
                        batch.Select(GmailMailReadProvider.ParseMessageKey).ToArray(),
                        addLabelIds,
                        removeLabelIds,
                        cancellationToken);
                    succeeded.AddRange(batch);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    GmailMailboxFailureKind kind = Classify(exception);
                    failed.AddRange(batch.Select(key => new GmailMailboxItemFailure(key, kind)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(distinctKeys, Classify(exception));
        }

        return new GmailMailboxMutationResult(succeeded, failed);
    }

    private async Task<MailCredential> LoadModifyCredentialAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        if (!IsGmail(account))
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Действие доступно только для Gmail.");
        }

        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            throw new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.");
        }

        if (!credential.HasGmailModifyScope)
        {
            throw new MailReadException(
                MailReadFailureKind.MutationNotAuthorized,
                "Чтобы управлять письмами, нужно снова разрешить доступ Google.");
        }

        return credential;
    }

    private static bool IsGmail(MailAccount? account) =>
        account is { Provider: MailProviderType.Gmail, IsEnabled: true };

    private static string[] NormalizeMessageKeys(IReadOnlyCollection<string> messageKeys) =>
        messageKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static GmailMailboxMutationResult Failure(
        IReadOnlyCollection<string> messageKeys,
        GmailMailboxFailureKind failureKind) =>
        new([], messageKeys.Select(key => new GmailMailboxItemFailure(key, failureKind)).ToArray());

    internal static GmailMailboxFailureKind Classify(Exception exception)
    {
        if (exception is MailReadException { FailureKind: MailReadFailureKind.ReauthorizationRequired }
            || GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            return GmailMailboxFailureKind.ReauthorizationRequired;
        }

        if (exception is MailReadException { FailureKind: MailReadFailureKind.MutationNotAuthorized }
            || exception is GoogleApiException { HttpStatusCode: HttpStatusCode.Forbidden })
        {
            return GmailMailboxFailureKind.NotAuthorized;
        }

        if (exception is HttpRequestException or IOException or TimeoutException
            || exception is GoogleApiException apiException
                && ((int)apiException.HttpStatusCode >= 500
                    || apiException.HttpStatusCode is HttpStatusCode.RequestTimeout
                        or (HttpStatusCode)429))
        {
            return GmailMailboxFailureKind.TransientFailure;
        }

        return GmailMailboxFailureKind.PermanentFailure;
    }
}
