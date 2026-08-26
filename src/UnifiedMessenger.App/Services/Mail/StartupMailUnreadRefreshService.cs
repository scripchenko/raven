using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Mail;

public interface IStartupMailUnreadRefreshService
{
    Task RefreshAsync(
        IEnumerable<MailAccount> accounts,
        CancellationToken cancellationToken = default);
}

public sealed class StartupMailUnreadRefreshService(
    IMailReadProviderFactory providerFactory,
    IUiDispatcher uiDispatcher) : IStartupMailUnreadRefreshService
{
    internal const int MaximumConcurrency = 2;

    public Task RefreshAsync(
        IEnumerable<MailAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        MailAccount[] enabledAccounts = accounts
            .Where(account => account.IsEnabled)
            .ToArray();

        return Parallel.ForEachAsync(
            enabledAccounts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrency
            },
            RefreshAccountAsync);

        async ValueTask RefreshAccountAsync(MailAccount account, CancellationToken token)
        {
            try
            {
                IMailReadProvider provider = providerFactory.Get(account.Provider);
                if (provider is not IMailInboxUnreadCountProvider unreadCountProvider)
                {
                    return;
                }

                int unreadCount = await unreadCountProvider
                    .GetInboxUnreadCountAsync(account, token)
                    .ConfigureAwait(false);

                uiDispatcher.Post(() =>
                {
                    if (!token.IsCancellationRequested && account.IsEnabled)
                    {
                        account.InboxUnreadCount = unreadCount;
                    }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Application shutdown cancels the one-shot startup refresh.
            }
            catch (Exception)
            {
                // Startup badge refresh is best-effort and isolated per account.
                // A later account activation or manual refresh can try again.
            }
        }
    }
}
