namespace UnifiedMessenger.App.Models;

public enum MailFolderKind
{
    Inbox,
    Sent,
    Drafts,
    AllMail,
    Spam,
    Trash,
    Starred
}

public sealed record MailFolder
{
    internal MailFolder(
        string key,
        string displayName,
        MailFolderKind kind,
        bool isAvailable,
        string providerLocator,
        bool supportsReadState)
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
    }

    public string Key { get; }
    public string DisplayName { get; }
    public MailFolderKind Kind { get; }
    public bool IsAvailable { get; }
    public bool SupportsReadState { get; }

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

    public static MailFolder Create(
        MailFolderKind kind,
        string providerLocator,
        bool supportsReadState = true) =>
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
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            kind,
            true,
            providerLocator,
            supportsReadState && kind is not MailFolderKind.Drafts);

    public static MailFolder Inbox(string providerLocator = "INBOX") =>
        Create(MailFolderKind.Inbox, providerLocator);
}
