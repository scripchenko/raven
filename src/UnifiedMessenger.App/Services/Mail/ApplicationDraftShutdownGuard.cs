using System.Windows;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.App.Services.Mail;

public interface IApplicationDraftShutdownGuard
{
    Task<bool> TryPrepareExplicitExitAsync(CancellationToken cancellationToken = default);
    bool PersistSessionEndingRecovery();
}

public interface IDraftShutdownFailurePresenter
{
    void Show(ServerDraftFlushResult result);
    void ShowRecoveryWriteFailure();
}

public sealed class ApplicationDraftShutdownGuard(
    MailComposeViewModel compose,
    IApplicationExitCoordinator exitCoordinator,
    IWindowActivationService windowActivation,
    IDraftShutdownFailurePresenter presenter) : IApplicationDraftShutdownGuard
{
    public async Task<bool> TryPrepareExplicitExitAsync(CancellationToken cancellationToken = default)
    {
        ServerDraftFlushResult result;
        try
        {
            result = await compose.FlushPendingServerDraftsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = new(ServerDraftFlushStatus.TimedOutOrCanceled, []);
        }
        catch (Exception)
        {
            result = new(ServerDraftFlushStatus.Failed, []);
        }

        if (result.CanShutdown)
        {
            return true;
        }

        exitCoordinator.CancelShutdownAttempt();
        windowActivation.ShowAndActivate();
        presenter.Show(result);
        return false;
    }

    public bool PersistSessionEndingRecovery()
    {
        bool persisted;
        try
        {
            persisted = compose.PersistDirtyManagedImapDraftRecovery();
        }
        catch (Exception)
        {
            persisted = false;
        }

        if (!persisted)
        {
            windowActivation.ShowAndActivate();
            presenter.ShowRecoveryWriteFailure();
        }
        return persisted;
    }
}

public sealed class WpfDraftShutdownFailurePresenter : IDraftShutdownFailurePresenter
{
    public void Show(ServerDraftFlushResult result)
    {
        string detail = result.Status switch
        {
            ServerDraftFlushStatus.Ambiguous =>
                "Lantern не может подтвердить сохранение черновика. Проверьте папку «Черновики» и повторите выход.",
            ServerDraftFlushStatus.TimedOutOrCanceled =>
                "Сохранение черновика не завершилось вовремя. Проверьте подключение и повторите выход.",
            _ => "Не удалось сохранить черновик. Проверьте подключение и повторите выход."
        };
        ShowMessage(detail);
    }

    public void ShowRecoveryWriteFailure() => ShowMessage(
        "Не удалось безопасно сохранить локальную копию черновика. Завершение сеанса отменено.");

    private static void ShowMessage(string text)
    {
        Window? owner = System.Windows.Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            _ = System.Windows.MessageBox.Show(owner, text, "Черновик не сохранён", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            _ = System.Windows.MessageBox.Show(text, "Черновик не сохранён", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
