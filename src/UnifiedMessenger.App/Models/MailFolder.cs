namespace UnifiedMessenger.App.Models;

public enum MailFolderKind
{
    Inbox,
    Sent,
    Drafts,
    AllMail,
    Spam,
    Trash,
    Starred,
    UserLabel,
    Archive
}

public sealed record MailFolder
{
    internal MailFolder(
        string key,
        string displayName,
        MailFolderKind kind,
        bool isAvailable,
        string providerLocator,
        bool supportsReadState,
        bool showsUserLabelSectionHeader = false,
        bool canAcceptArchive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerLocator);
        Key = key;
        DisplayName = displayName;
        Kind = kind;
        IsAvailable = isAvailable;
        ProviderLocator = providerLocator;
        SupportsReadState = supportsReadState;
        ShowsUserLabelSectionHeader = showsUserLabelSectionHeader;
        CanAcceptArchive = kind is MailFolderKind.Archive && canAcceptArchive;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public MailFolderKind Kind { get; }
    public bool IsAvailable { get; }
    public bool SupportsReadState { get; }
    public bool IsUserLabel => Kind is MailFolderKind.UserLabel;
    public bool ShowsUserLabelSectionHeader { get; }
    public bool CanAcceptArchive { get; }

    // Provider locators are deliberately not exposed to WPF bindings or public callers.
    internal string ProviderLocator { get; }
}

internal static class MailFolderCatalog
{
    public const string InboxKey = "system:inbox";
    public const string StarredKey = "system:starred";
    public const string SentKey = "system:sent";
    public const string DraftsKey = "system:drafts";
    public const string AllMailKey = "system:all-mail";
    public const string SpamKey = "system:spam";
    public const string TrashKey = "system:trash";
    public const string ArchiveKey = "system:archive";
    private const string GmailUserLabelKeyPrefix = "gmail:user-label:";

    public static MailFolder Create(
        MailFolderKind kind,
        string providerLocator,
        bool supportsReadState = true,
        bool canAcceptArchive = false) =>
        new(
            kind switch
            {
                MailFolderKind.Inbox => InboxKey,
                MailFolderKind.Starred => StarredKey,
                MailFolderKind.Sent => SentKey,
                MailFolderKind.Drafts => DraftsKey,
                MailFolderKind.AllMail => AllMailKey,
                MailFolderKind.Spam => SpamKey,
                MailFolderKind.Trash => TrashKey,
                MailFolderKind.Archive => ArchiveKey,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            kind switch
            {
                MailFolderKind.Inbox => "Входящие",
                MailFolderKind.Starred => "Помеченные",
                MailFolderKind.Sent => "Отправленные",
                MailFolderKind.Drafts => "Черновики",
                MailFolderKind.AllMail => "Вся почта",
                MailFolderKind.Spam => "Спам",
                MailFolderKind.Trash => "Корзина",
                MailFolderKind.Archive => "Архив",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            kind,
            true,
            providerLocator,
            supportsReadState && kind is not MailFolderKind.Drafts,
            canAcceptArchive: canAcceptArchive);

    public static MailFolder CreateUserLabel(
        string labelId,
        string displayName,
        bool showsSectionHeader = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new MailFolder(
            GmailUserLabelKeyPrefix + labelId,
            displayName,
            MailFolderKind.UserLabel,
            true,
            labelId,
            supportsReadState: true,
            showsSectionHeader);
    }

    public static MailFolder Inbox(string providerLocator = "INBOX") =>
        Create(MailFolderKind.Inbox, providerLocator);
}
