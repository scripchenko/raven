using System.IO;
using MailKit;
using MailKit.Net.Imap;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class YandexMailboxManagementTests
{
    [Theory]
    [InlineData(ImapCapabilities.Move, true)]
    [InlineData(ImapCapabilities.UidPlus, true)]
    [InlineData(ImapCapabilities.Move | ImapCapabilities.UidPlus, true)]
    [InlineData(ImapCapabilities.IMAP4rev1, false)]
    public void ArchiveMetadata_RequiresSafeMoveCapability(ImapCapabilities capabilities, bool expected)
    {
        var descriptor = MailKitImapInboxClient.DescribeArchiveFolder(
            "server/store-17", "INBOX", FolderAttributes.Archive | FolderAttributes.HasNoChildren, capabilities);
        Assert.NotNull(descriptor);
        Assert.Equal("server/store-17", descriptor.FullName);
        Assert.Equal(expected, descriptor.CanAcceptArchive);
    }

    [Theory]
    [InlineData(FolderAttributes.HasNoChildren)]
    [InlineData(FolderAttributes.Archive | FolderAttributes.NoSelect)]
    [InlineData(FolderAttributes.Archive | FolderAttributes.NonExistent)]
    [InlineData(FolderAttributes.Archive | FolderAttributes.Trash)]
    [InlineData(FolderAttributes.Archive | FolderAttributes.Junk)]
    [InlineData(FolderAttributes.All)]
    public void ArchiveMetadata_RejectsNamesAndUnsafeRoles(FolderAttributes attributes)
    {
        foreach (string name in new[] { "Archive", "Архив", "server/store-17" })
        {
            Assert.Null(MailKitImapInboxClient.DescribeArchiveFolder(name, "INBOX", attributes,
                ImapCapabilities.Move | ImapCapabilities.UidPlus));
        }
    }

    [Fact]
    public void ArchiveMetadata_RejectsInboxAlias()
    {
        Assert.Null(MailKitImapInboxClient.DescribeArchiveFolder("INBOX", "INBOX",
            FolderAttributes.Archive, ImapCapabilities.Move));
    }

    [Theory]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Archive, MailFolderKind.Archive)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Trash, MailFolderKind.Trash)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Spam, MailFolderKind.Spam)]
    [InlineData(MailFolderKind.Spam, MailMailboxAction.Trash, MailFolderKind.Trash)]
    [InlineData(MailFolderKind.Spam, MailMailboxAction.NotSpam, MailFolderKind.Inbox)]
    [InlineData(MailFolderKind.Trash, MailMailboxAction.Restore, MailFolderKind.Inbox)]
    [InlineData(MailFolderKind.Archive, MailMailboxAction.Trash, MailFolderKind.Trash)]
    public async Task Move_UsesExactUidsAndServerFolder(MailFolderKind source, MailMailboxAction action, MailFolderKind destination)
    {
        Fixture fixture = new();
        string[] keys = [Key(source, 4), Key(source, 9)];
        MailMailboxMutationResult result = await fixture.Apply(source, action, keys);

        Assert.Equal(keys, result.SucceededMessageKeys);
        Assert.Empty(result.FailedMessageKeys);
        Assert.Equal(destination, fixture.Session.Destination);
        Assert.Equal(Folder(source).ProviderLocator, fixture.Session.Source?.ProviderLocator);
        Assert.Equal(["move:4", "move:9"], fixture.Session.Writes);
        Assert.True(result.RequiresRefresh);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<MailMailboxMutationItemResult>>(result.ItemResults),
            item =>
            {
                Assert.Equal(keys[0], item.SourceMessageKey);
                Assert.Equal(MailMailboxMutationItemStatus.Succeeded, item.Status);
                Assert.Equal(Key(destination, 1004, 17), item.DestinationMessageKey);
            },
            item =>
            {
                Assert.Equal(keys[1], item.SourceMessageKey);
                Assert.Equal(MailMailboxMutationItemStatus.Succeeded, item.Status);
                Assert.Equal(Key(destination, 1009, 17), item.DestinationMessageKey);
            });
        if (destination is MailFolderKind.Inbox)
        {
            Assert.True(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:17", "1004"));
            Assert.False(fixture.Tracker.IsLocalInboxMove(Guid.NewGuid(), "imap-inbox:17", "1004"));
            Assert.False(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:18", "1004"));
        }
    }

    [Theory]
    [InlineData(MailMailboxAction.Read, MessageFlags.Seen, true)]
    [InlineData(MailMailboxAction.Unread, MessageFlags.Seen, false)]
    public async Task ReadState_IsUidSafeAndDoesNotMove(MailMailboxAction action, MessageFlags flag, bool add)
    {
        Fixture fixture = new();
        MailMailboxMutationResult result = await fixture.Apply(MailFolderKind.Inbox, action, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Single(result.SucceededMessageKeys);
        Assert.Null(fixture.Session.Destination);
        Assert.Equal([$"flag:4:{flag}:{add}"], fixture.Session.Writes);
    }

    [Fact]
    public async Task UidPlusFallback_CopiesBeforeDeletingAndExpungesOnlySelectedUid()
    {
        Fixture fixture = new();
        fixture.Session.SupportsMove = false;
        var result = await fixture.Apply(MailFolderKind.Spam, MailMailboxAction.NotSpam, [Key(MailFolderKind.Spam, 9)]);
        Assert.Single(result.SucceededMessageKeys);
        Assert.Equal(["copy:9", "flag:9:Deleted:True", "uid-expunge:9"], fixture.Session.Writes);
        Assert.True(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:17", "1009"));
    }

    [Fact]
    public async Task FallbackWithoutUidPlus_DoesNotCopyOrDeleteAnything()
    {
        Fixture fixture = new();
        fixture.Session.SupportsMove = false;
        fixture.Session.SupportsUidPlus = false;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Empty(fixture.Session.Writes);
        Assert.NotNull(result.UserMessage);
    }

    [Fact]
    public async Task MissingArchiveMetadata_DoesNotGuessOrCreateAFolder()
    {
        Fixture fixture = new();
        fixture.Session.MissingDestination = true;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Archive, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Empty(fixture.Session.Writes);
        Assert.NotNull(result.UserMessage);
    }

    [Theory]
    [InlineData("imap:0:8:4")]
    [InlineData("imap:0:0:4")]
    [InlineData("imap:4:9:4")]
    [InlineData("imap:0:9:0")]
    [InlineData("gmail:4")]
    public async Task InvalidOrStaleIdentity_NeverMutates(string key)
    {
        Fixture fixture = new();
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [key]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Empty(fixture.Session.Writes);
    }

    [Fact]
    public async Task MixedUidValidity_RejectsWholeBatchBeforeConnecting()
    {
        Fixture fixture = new();
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [Key(MailFolderKind.Inbox, 4), "imap:0:8:9"]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Null(fixture.Session.Username);
    }

    [Fact]
    public async Task MissingUid_ReturnsPartialSuccessWithoutPretendingItMoved()
    {
        Fixture fixture = new();
        fixture.Session.MissingUid = 9;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [Key(MailFolderKind.Inbox, 4), Key(MailFolderKind.Inbox, 9)]);
        Assert.Equal([Key(MailFolderKind.Inbox, 4)], result.SucceededMessageKeys);
        Assert.Equal([Key(MailFolderKind.Inbox, 9)], result.FailedMessageKeys);
        Assert.Equal(["move:4"], fixture.Session.Writes);
        Assert.Equal(
            [MailMailboxMutationItemStatus.Succeeded, MailMailboxMutationItemStatus.Failed],
            result.ItemResults!.Select(item => item.Status));
    }

    [Fact]
    public async Task DisconnectAfterCopy_DoesNotRetryOrExpungeAndSuppressesKnownInboxUid()
    {
        Fixture fixture = new();
        fixture.Session.SupportsMove = false;
        fixture.Session.FailDeletedStore = true;
        var result = await fixture.Apply(MailFolderKind.Trash, MailMailboxAction.Restore, [Key(MailFolderKind.Trash, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Equal(["copy:4", "flag:4:Deleted:True"], fixture.Session.Writes);
        Assert.True(result.RequiresRefresh);
        Assert.True(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:17", "1004"));
        Assert.False(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
        MailMailboxMutationItemResult item = Assert.Single(result.ItemResults!);
        Assert.Equal(MailMailboxMutationItemStatus.Ambiguous, item.Status);
        Assert.Equal(Key(MailFolderKind.Inbox, 1004, 17), item.DestinationMessageKey);
    }

    [Fact]
    public async Task AmbiguousInboxMove_RequestsOneSafeBaselineAndNeverRetries()
    {
        Fixture fixture = new();
        fixture.Session.FailMove = true;
        var result = await fixture.Apply(MailFolderKind.Trash, MailMailboxAction.Restore, [Key(MailFolderKind.Trash, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Equal(["move:4"], fixture.Session.Writes);
        Assert.True(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
        Assert.False(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
        Assert.Equal(MailMailboxMutationItemStatus.Ambiguous, Assert.Single(result.ItemResults!).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeMoveWithoutDestinationUid_IsSuccessfulAndUsesSafeInboxBaseline(bool supportsUidPlus)
    {
        Fixture fixture = new();
        fixture.Session.SupportsUidPlus = supportsUidPlus;
        fixture.Session.MissingMapping = true;

        MailMailboxMutationResult result = await fixture.Apply(
            MailFolderKind.Spam,
            MailMailboxAction.NotSpam,
            [Key(MailFolderKind.Spam, 4)]);

        Assert.Equal([Key(MailFolderKind.Spam, 4)], result.SucceededMessageKeys);
        Assert.Empty(result.FailedMessageKeys);
        Assert.Equal(["move:4"], fixture.Session.Writes);
        MailMailboxMutationItemResult item = Assert.Single(result.ItemResults!);
        Assert.Equal(MailMailboxMutationItemStatus.Succeeded, item.Status);
        Assert.Null(item.DestinationMessageKey);
        Assert.True(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
        Assert.False(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
    }

    [Fact]
    public async Task FailureDuringSecondMove_ReportsConfirmedAmbiguousAndNotAttemptedWithoutRetry()
    {
        Fixture fixture = new();
        fixture.Session.FailMoveUid = 9;
        string[] keys =
        [
            Key(MailFolderKind.Spam, 4),
            Key(MailFolderKind.Spam, 9),
            Key(MailFolderKind.Spam, 12)
        ];

        MailMailboxMutationResult result = await fixture.Apply(
            MailFolderKind.Spam,
            MailMailboxAction.NotSpam,
            keys);

        Assert.Equal([keys[0]], result.SucceededMessageKeys);
        Assert.Equal([keys[1], keys[2]], result.FailedMessageKeys);
        Assert.Equal(["move:4", "move:9"], fixture.Session.Writes);
        Assert.Collection(
            result.ItemResults!,
            item =>
            {
                Assert.Equal(keys[0], item.SourceMessageKey);
                Assert.Equal(MailMailboxMutationItemStatus.Succeeded, item.Status);
                Assert.Equal(Key(MailFolderKind.Inbox, 1004, 17), item.DestinationMessageKey);
            },
            item =>
            {
                Assert.Equal(keys[1], item.SourceMessageKey);
                Assert.Equal(MailMailboxMutationItemStatus.Ambiguous, item.Status);
            },
            item =>
            {
                Assert.Equal(keys[2], item.SourceMessageKey);
                Assert.Equal(MailMailboxMutationItemStatus.NotAttempted, item.Status);
            });
        Assert.True(fixture.Tracker.IsLocalInboxMove(
            fixture.Account.Id,
            "imap-inbox:17",
            "1004"));
        Assert.True(fixture.Tracker.ConsumeBaselineRequest(fixture.Account.Id));
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.MailRu)]
    [InlineData(MailProviderType.GenericImap)]
    public async Task OtherProviders_NeverUseYandexMutations(MailProviderType provider)
    {
        Fixture fixture = new();
        fixture.Account.Provider = provider;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Empty(fixture.Credentials.ReadKeys);
        Assert.Null(fixture.Session.Username);
    }

    [Fact]
    public async Task CredentialAndServerIdentity_AreAccountScoped()
    {
        Fixture first = new();
        Fixture second = new();
        await first.Apply(MailFolderKind.Inbox, MailMailboxAction.Read, [Key(MailFolderKind.Inbox, 4)]);
        await second.Apply(MailFolderKind.Inbox, MailMailboxAction.Read, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Equal([first.Account.CredentialKey], first.Credentials.ReadKeys);
        Assert.Equal([second.Account.CredentialKey], second.Credentials.ReadKeys);
        Assert.Equal(first.Account.EmailAddress, first.Session.Username);
        Assert.Equal(second.Account.EmailAddress, second.Session.Username);
        Assert.NotEqual(first.Session.Username, second.Session.Username);
    }

    [Fact]
    public async Task InboxMove_UsesUidValidityFromServerCopyUidResponse()
    {
        Fixture fixture = new();
        fixture.Session.DestinationValidity = 25;
        var result = await fixture.Apply(MailFolderKind.Trash, MailMailboxAction.Restore, [Key(MailFolderKind.Trash, 4)]);
        Assert.Single(result.SucceededMessageKeys);
        Assert.True(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:25", "1004"));
        Assert.False(fixture.Tracker.IsLocalInboxMove(fixture.Account.Id, "imap-inbox:17", "1004"));
    }

    [Fact]
    public async Task FallbackWithoutCopyUidMapping_DoesNotDeleteSource()
    {
        Fixture fixture = new();
        fixture.Session.SupportsMove = false;
        fixture.Session.MissingMapping = true;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Trash, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Equal(["copy:4"], fixture.Session.Writes);
        Assert.True(result.RequiresRefresh);
    }

    [Fact]
    public async Task UnsupportedPermanentFlag_IsRejectedBeforeStore()
    {
        Fixture fixture = new();
        fixture.Session.PermanentFlags = MessageFlags.Deleted;
        var result = await fixture.Apply(MailFolderKind.Inbox, MailMailboxAction.Read, [Key(MailFolderKind.Inbox, 4)]);
        Assert.Empty(result.SucceededMessageKeys);
        Assert.Empty(fixture.Session.Writes);
    }

    [Fact]
    public void TrashHasRestoreButNoDelete_AndDraftsCannotBeArchived()
    {
        Fixture fixture = new();
        Assert.True(fixture.Service.CanApply(MailFolderKind.Trash, MailMailboxAction.Restore));
        Assert.False(fixture.Service.CanApply(MailFolderKind.Trash, MailMailboxAction.Trash));
        Assert.False(fixture.Service.CanApply(MailFolderKind.Drafts, MailMailboxAction.Archive));
    }

    private static string Key(MailFolderKind kind, uint uid, uint uidValidity = 9) =>
        ImapMailReadProvider.CreateMessageKey(kind, uidValidity, uid);
    private static MailFolder Folder(MailFolderKind kind) => MailFolderCatalog.Create(kind, $"server/{kind}");

    private sealed class Fixture
    {
        public MailAccount Account { get; } = new() { Id = Guid.NewGuid(), Provider = MailProviderType.Yandex,
            EmailAddress = $"{Guid.NewGuid():N}@example.test", CredentialKey = Guid.NewGuid().ToString("N"), IsEnabled = true };
        public CredentialStore Credentials { get; } = new();
        public Session Session { get; } = new();
        public ImapMailboxChangeTracker Tracker { get; } = new();
        public ImapMailboxManagementService Service { get; }
        public Fixture() => Service = new(Credentials, new MailProviderFactory([new YandexMailProvider(new Validator())]), new SessionFactory(Session), Tracker);
        public Task<MailMailboxMutationResult> Apply(MailFolderKind folder, MailMailboxAction action, string[] keys) => Service.ApplyAsync(Account, Folder(folder), keys, action);
    }

    private sealed class SessionFactory(Session session) : IImapMailboxSessionFactory
    {
        public IImapMailboxSession Create() => session;
    }

    private sealed class Session : IImapMailboxSession
    {
        public bool SupportsMove { get; set; } = true;
        public bool SupportsUidPlus { get; set; } = true;
        public bool MissingDestination { get; set; }
        public bool FailDeletedStore { get; set; }
        public bool FailMove { get; set; }
        public uint? FailMoveUid { get; set; }
        public bool MissingMapping { get; set; }
        public uint DestinationValidity { get; set; } = 17;
        public MessageFlags PermanentFlags { get; set; } = MessageFlags.Seen | MessageFlags.Deleted;
        public uint? MissingUid { get; set; }
        public string? Username { get; private set; }
        public MailFolderKind? Destination { get; private set; }
        public MailFolder? Source { get; private set; }
        public List<string> Writes { get; } = [];
        public Task ConnectAsync(MailServerSettings server, string secret, CancellationToken token) { Username = server.Username; return Task.CompletedTask; }
        public Task ResolveDestinationAsync(MailFolderKind kind, CancellationToken token)
        {
            Destination = kind;
            return MissingDestination ? Task.FromException(new NotSupportedException()) : Task.CompletedTask;
        }
        public Task<ImapMailboxSourceState> OpenSourceAsync(MailFolder source, CancellationToken token)
        {
            Source = source;
            return Task.FromResult(new ImapMailboxSourceState(9, PermanentFlags));
        }
        public Task<bool> ExistsAsync(uint uid, CancellationToken token) => Task.FromResult(uid != MissingUid);
        public Task<UniqueId?> MoveAsync(uint uid, CancellationToken token)
        {
            Writes.Add($"move:{uid}");
            return FailMove || FailMoveUid == uid ? Task.FromException<UniqueId?>(new IOException())
                : Task.FromResult<UniqueId?>(MissingMapping ? null : new UniqueId(DestinationValidity, uid + 1000));
        }
        public Task<UniqueId?> CopyAsync(uint uid, CancellationToken token)
        {
            Writes.Add($"copy:{uid}");
            return Task.FromResult<UniqueId?>(MissingMapping ? null : new UniqueId(DestinationValidity, uid + 1000));
        }
        public Task SetFlagAsync(uint uid, MessageFlags flag, bool add, CancellationToken token)
        {
            Writes.Add($"flag:{uid}:{flag}:{add}");
            return FailDeletedStore && flag is MessageFlags.Deleted ? Task.FromException(new IOException()) : Task.CompletedTask;
        }
        public Task ExpungeUidAsync(uint uid, CancellationToken token) { Writes.Add($"uid-expunge:{uid}"); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class CredentialStore : IMailCredentialStore
    {
        public List<string> ReadKeys { get; } = [];
        public Task<MailCredential?> LoadAsync(string key, CancellationToken cancellationToken = default)
        { ReadKeys.Add(key); return Task.FromResult<MailCredential?>(MailCredential.CreatePassword("synthetic-password")); }
        public Task SaveAsync(string key, MailCredential value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Validator : IMailConnectionValidator
    {
        public Task<MailConnectionValidationResult> ValidateAsync(MailConnectionSettings settings, string emailAddress, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
