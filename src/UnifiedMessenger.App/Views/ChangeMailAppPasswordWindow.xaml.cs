using System.Windows;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.Views;

public partial class ChangeMailAppPasswordWindow : Window
{
    private readonly MailAccount _account;
    private readonly IMailAccountProvisioningService _provisioningService;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    public ChangeMailAppPasswordWindow(
        MailAccount account,
        IMailAccountProvisioningService provisioningService)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _provisioningService = provisioningService
            ?? throw new ArgumentNullException(nameof(provisioningService));
        EmailAddress = account.EmailAddress;
        PasswordGuidance = account.Provider switch
        {
            MailProviderType.MailRu =>
                "Используйте новый пароль для внешнего приложения Mail.ru с полным доступом к Почте. Доступ по IMAP/SMTP должен быть включён.",
            MailProviderType.Yandex =>
                "Используйте новый пароль приложения Яндекса. Он будет проверен через IMAP и SMTP до замены сохранённых учётных данных.",
            _ =>
                "Новый пароль будет проверен через IMAP и SMTP до замены сохранённых учётных данных."
        };
        InitializeComponent();
        DataContext = this;
    }

    public string EmailAddress { get; }
    public string PasswordGuidance { get; }

    private async void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_isBusy)
        {
            return;
        }

        string password = PasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            StatusText.Text = "Введите новый пароль приложения.";
            PasswordBox.Focus();
            return;
        }

        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        SaveButton.IsEnabled = false;
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = "Проверяем IMAP и SMTP…";
        try
        {
            MailAccountPasswordReplacementResult result =
                await _provisioningService.ReplaceAppPasswordAsync(
                    _account,
                    password,
                    _operationCancellation.Token);
            if (!result.IsSuccess)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                StatusText.Text = result.UserMessage;
                return;
            }

            PasswordBox.Clear();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "Проверка нового пароля отменена.";
        }
        catch
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "Не удалось проверить и сохранить новый пароль приложения.";
        }
        finally
        {
            password = string.Empty;
            PasswordBox.Clear();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _isBusy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        PasswordBox.Clear();
        if (_isBusy)
        {
            _operationCancellation?.Cancel();
            StatusText.Text = "Отменяем проверку…";
            return;
        }

        DialogResult = false;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        PasswordBox.Clear();
        _operationCancellation?.Cancel();
        base.OnClosed(eventArgs);
    }
}
