using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public enum MailMailboxAction
{
    Archive, Trash, Spam, NotSpam, Restore, Read, Unread
}

public enum MailMailboxMutationItemStatus
{
    Succeeded,
    Failed,
    Ambiguous,
    NotAttempted
}

public sealed record MailMailboxMutationItemResult(
    string SourceMessageKey,
    MailMailboxMutationItemStatus Status,
    string? DestinationMessageKey = null);

public sealed record MailMailboxMutationResult(
    IReadOnlyList<string> SucceededMessageKeys,
    IReadOnlyList<string> FailedMessageKeys,
    MailFolderKind? DestinationFolder,
    string? UserMessage = null,
    bool RequiresRefresh = false,
    IReadOnlyList<MailMailboxMutationItemResult>? ItemResults = null);

public interface IMailMailboxManagementService
{
    bool Supports(MailProviderType provider);
    bool CanApply(MailFolderKind source, MailMailboxAction action);
    Task<MailMailboxMutationResult> ApplyAsync(
        MailAccount account,
        MailFolder source,
        IReadOnlyCollection<string> messageKeys,
        MailMailboxAction action,
        CancellationToken cancellationToken = default);
}
