using System.Collections.Concurrent;

namespace UnifiedMessenger.App.Services.Mail;

// Serializes a local mutation with polling of the same account. Destination UIDs
// are ignored by the new-mail detector, without hiding unrelated incoming mail.
public sealed class ImapMailboxChangeTracker
{
    private readonly ConcurrentDictionary<Guid, AccountState> _accounts = new();

    internal async Task<IDisposable> EnterAsync(Guid accountId, CancellationToken cancellationToken)
    {
        AccountState state = _accounts.GetOrAdd(accountId, _ => new());
        await state.Gate.WaitAsync(cancellationToken);
        return new Lease(state.Gate);
    }

    internal void RecordInboxMove(Guid accountId, uint uidValidity, IEnumerable<uint> uids)
    {
        AccountState state = _accounts.GetOrAdd(accountId, _ => new());
        foreach (uint uid in uids)
        {
            state.MovedToInbox.Add((uidValidity, uid));
        }
    }

    internal void RequireBaseline(Guid accountId) =>
        _accounts.GetOrAdd(accountId, _ => new()).NeedsBaseline = true;

    internal bool ConsumeBaselineRequest(Guid accountId)
    {
        AccountState state = _accounts.GetOrAdd(accountId, _ => new());
        bool value = state.NeedsBaseline;
        state.NeedsBaseline = false;
        return value;
    }

    internal bool IsLocalInboxMove(Guid accountId, string scope, string identity) =>
        scope.StartsWith("imap-inbox:", StringComparison.Ordinal)
        && uint.TryParse(scope.AsSpan("imap-inbox:".Length), out uint validity)
        && uint.TryParse(identity, out uint uid)
        && _accounts.TryGetValue(accountId, out AccountState? state)
        && state.MovedToInbox.Contains((validity, uid));

    internal void RetireThrough(Guid accountId, string scope, uint highWaterUid)
    {
        if (_accounts.TryGetValue(accountId, out AccountState? state)
            && scope.StartsWith("imap-inbox:", StringComparison.Ordinal)
            && uint.TryParse(scope.AsSpan("imap-inbox:".Length), out uint validity))
        {
            state.MovedToInbox.RemoveWhere(item => item.Validity != validity || item.Uid <= highWaterUid);
        }
    }

    private sealed class AccountState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HashSet<(uint Validity, uint Uid)> MovedToInbox { get; } = [];
        public bool NeedsBaseline { get; set; }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
