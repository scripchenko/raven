using System.Windows;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Localization;

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
                Localizer.Instance.Get("Use a new Mail.ru external-app password with full Mail access. IMAP/SMTP access must be enabled."),
            MailProviderType.Yandex =>
                Localizer.Instance.Get("Use a new Yandex app password. It will be verified through IMAP and SMTP before replacing the saved credentials."),
            _ =>
                Localizer.Instance.Get("The new password will be verified through IMAP and SMTP before replacing the saved credentials.")
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
            StatusText.Text = Localizer.Instance.Get("Enter a new app password.");
            PasswordBox.Focus();
            return;
        }

        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        SaveButton.IsEnabled = false;
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = Localizer.Instance.Get("Checking IMAP and SMTP…");
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
            StatusText.Text = Localizer.Instance.Get("New password verification canceled.");
        }
        catch
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = Localizer.Instance.Get("Could not verify and save the new app password.");
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
            StatusText.Text = Localizer.Instance.Get("Canceling verification…");
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
