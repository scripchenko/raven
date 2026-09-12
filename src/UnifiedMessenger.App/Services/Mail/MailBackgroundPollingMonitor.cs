using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailNewMessageDetectedEventArgs(
    Guid mailAccountId,
    int newMessageCount) : EventArgs
{
    public Guid MailAccountId { get; } = mailAccountId;
    public int NewMessageCount { get; } = Math.Max(1, newMessageCount);
}

public interface IMailBackgroundPollingMonitor : IDisposable
{
    event EventHandler<MailNewMessageDetectedEventArgs>? MailNewMessageDetected;

    void Start();
    void BeginShutdown();
    Task StopAsync();
}

internal sealed record MailInboxTechnicalSnapshot(
    int UnreadCount,
    string IdentityScope,
    IReadOnlyCollection<string> MessageIdentities);

internal interface IMailInboxTechnicalSnapshotProvider
{
    Task<MailInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
        MailAccount account,
        CancellationToken cancellationToken = default);
}

internal sealed record MailHistoryPollResult(
    int UnreadCount,
    ulong HistoryCursor,
    IReadOnlyCollection<string> NewMessageIdentities,
    bool IsRebaseline = false);

internal interface IMailNewMessageHistoryProvider
{
    Task<MailHistoryPollResult> PollHistoryAsync(
        MailAccount account,
        ulong? historyCursor,
        CancellationToken cancellationToken = default);
}

public sealed class MailBackgroundPollingMonitor(
    IApplicationSettingsStore settingsStore,
    IMailReadProviderFactory providerFactory,
    IUiDispatcher uiDispatcher,
    TimeProvider timeProvider) : IMailBackgroundPollingMonitor
{
    public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, MailInboxBaseline> _baselines = [];
    private readonly Dictionary<Guid, ulong> _historyCursors = [];
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _lifecycleSync = new();
    private Task? _runTask;
    private bool _shutdownStarted;
    private bool _disposed;

    public event EventHandler<MailNewMessageDetectedEventArgs>? MailNewMessageDetected;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleSync)
        {
            if (_shutdownStarted || _runTask is not null)
            {
                return;
            }

            _runTask = RunAsync(_shutdownCancellation.Token);
        }
    }

    public void BeginShutdown()
    {
        lock (_lifecycleSync)
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            _shutdownCancellation.Cancel();
        }
    }

    public async Task StopAsync()
    {
        BeginShutdown();
        Task? runTask;
        lock (_lifecycleSync)
        {
            runTask = _runTask;
        }

        if (runTask is null)
        {
            return;
        }

        try
        {
            await runTask;
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await _pollGate.WaitAsync(cancellationToken);
        try
        {
            MailAccount[] enabledAccounts = await uiDispatcher.InvokeAsync(
                () => settingsStore.Current.MailAccounts
                    .Where(account => account.IsEnabled)
                    .ToArray());
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<Guid> enabledIds = enabledAccounts.Select(account => account.Id).ToHashSet();
            foreach (Guid accountId in _baselines.Keys.Where(id => !enabledIds.Contains(id)).ToArray())
            {
                _baselines.Remove(accountId);
            }

            foreach (Guid accountId in _historyCursors.Keys.Where(id => !enabledIds.Contains(id)).ToArray())
            {
                _historyCursors.Remove(accountId);
            }

            foreach (MailAccount account in enabledAccounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PollAccountAsync(account, cancellationToken);
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BeginShutdown();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await PollOnceAsync(cancellationToken);
                await Task.Delay(PollingInterval, timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PollAccountAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            IMailReadProvider provider = providerFactory.Get(account.Provider);
            if (account.Provider is MailProviderType.Gmail)
            {
                if (provider is IMailNewMessageHistoryProvider historyProvider)
                {
                    await PollHistoryAccountAsync(account, historyProvider, cancellationToken);
                }

                return;
            }

            if (provider is not IMailInboxTechnicalSnapshotProvider snapshotProvider)
            {
                return;
            }

            MailInboxTechnicalSnapshot snapshot = await snapshotProvider
                .GetInboxTechnicalSnapshotAsync(account, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(snapshot.IdentityScope))
            {
                return;
            }

            HashSet<string> identities = snapshot.MessageIdentities
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToHashSet(StringComparer.Ordinal);
            int newMessageCount = 0;
            if (_baselines.TryGetValue(account.Id, out MailInboxBaseline? previous)
                && string.Equals(previous.IdentityScope, snapshot.IdentityScope, StringComparison.Ordinal))
            {
                newMessageCount = identities.Count(identity => !previous.MessageIdentities.Contains(identity));
            }

            _baselines[account.Id] = new MailInboxBaseline(snapshot.IdentityScope, identities);
            int unreadCount = Math.Max(0, snapshot.UnreadCount);
            uiDispatcher.Post(() => ApplySuccessfulPoll(account.Id, unreadCount, newMessageCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A background provider/network failure is isolated to this account and poll.
        }
    }

    private async Task PollHistoryAccountAsync(
        MailAccount account,
        IMailNewMessageHistoryProvider historyProvider,
        CancellationToken cancellationToken)
    {
        bool hasCursor = _historyCursors.TryGetValue(account.Id, out ulong cursor);
        MailHistoryPollResult result = await historyProvider.PollHistoryAsync(
            account,
            hasCursor ? cursor : null,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string[] newMessageIdentities = result.NewMessageIdentities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (hasCursor && !result.IsRebaseline && result.HistoryCursor < cursor)
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Gmail вернул некорректный идентификатор истории.");
        }

        if (hasCursor
            && !result.IsRebaseline
            && result.HistoryCursor == cursor
            && newMessageIdentities.Length > 0)
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Gmail не продвинул идентификатор истории.");
        }

        if (!await IsAccountStillEnabledAsync(account.Id))
        {
            _historyCursors.Remove(account.Id);
            return;
        }

        _baselines.Remove(account.Id);
        _historyCursors[account.Id] = result.HistoryCursor;
        int newMessageCount = !hasCursor || result.IsRebaseline
            ? 0
            : newMessageIdentities.Length;
        int unreadCount = Math.Max(0, result.UnreadCount);
        uiDispatcher.Post(() => ApplySuccessfulPoll(account.Id, unreadCount, newMessageCount));
    }

    private Task<bool> IsAccountStillEnabledAsync(Guid accountId) =>
        uiDispatcher.InvokeAsync(() => settingsStore.Current.MailAccounts.Any(
            candidate => candidate.Id == accountId && candidate.IsEnabled));

    private void ApplySuccessfulPoll(Guid accountId, int unreadCount, int newMessageCount)
    {
        if (_shutdownStarted)
        {
            return;
        }

        MailAccount? account = settingsStore.Current.MailAccounts.FirstOrDefault(
            candidate => candidate.Id == accountId && candidate.IsEnabled);
        if (account is null)
        {
            return;
        }

        account.InboxUnreadCount = unreadCount;
        if (newMessageCount > 0)
        {
            MailNewMessageDetected?.Invoke(
                this,
                new MailNewMessageDetectedEventArgs(accountId, newMessageCount));
        }
    }

    private sealed record MailInboxBaseline(
        string IdentityScope,
        HashSet<string> MessageIdentities);
}
