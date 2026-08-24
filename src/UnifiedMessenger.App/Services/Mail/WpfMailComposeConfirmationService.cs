using System.Windows;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class WpfMailComposeConfirmationService : IMailComposeConfirmationService
{
    public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MessageBoxResult result = Show(
            "Тема и текст письма пусты. Всё равно отправить?",
            "Пустое письмо");
        return Task.FromResult(result is MessageBoxResult.Yes);
    }

    public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MessageBoxResult result = Show(
            "Удалить неотправленное письмо?",
            "Отмена письма");
        return Task.FromResult(result is MessageBoxResult.Yes);
    }

    private static MessageBoxResult Show(string text, string title)
    {
        Window? owner = WpfApplication.Current?.MainWindow;
        return owner is null
            ? WpfMessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : WpfMessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
    }
}
