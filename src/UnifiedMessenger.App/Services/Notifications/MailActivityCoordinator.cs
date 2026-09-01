using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface IMailActivityCoordinator
{
    event EventHandler? ActivityChanged;

    bool HasActivity(IEnumerable<MailAccount> accounts);
    void MarkNewMail(MailAccount account);
    void Clear(MailAccount account);
    void Reset(IEnumerable<MailAccount> accounts);
}

public sealed class MailActivityCoordinator : IMailActivityCoordinator
{
    public event EventHandler? ActivityChanged;

    public bool HasActivity(IEnumerable<MailAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.Any(account => account.IsEnabled && account.HasNewMailActivity);
    }

    public void MarkNewMail(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsEnabled || account.HasNewMailActivity)
        {
            return;
        }

        account.HasNewMailActivity = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.HasNewMailActivity)
        {
            return;
        }

        account.HasNewMailActivity = false;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(IEnumerable<MailAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        bool changed = false;
        foreach (MailAccount account in accounts)
        {
            changed |= account.HasNewMailActivity;
            account.HasNewMailActivity = false;
        }

        if (changed)
        {
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
