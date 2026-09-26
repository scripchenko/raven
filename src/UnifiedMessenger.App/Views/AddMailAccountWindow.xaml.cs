using System.Windows;
using System.Windows.Controls;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Localization;

namespace UnifiedMessenger.App.Views;

public partial class AddMailAccountWindow : Window
{
    private readonly IMailAccountProvisioningService _provisioningService;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    public AddMailAccountWindow(
        IMailProviderFactory providerFactory,
        IMailAccountProvisioningService provisioningService)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        _provisioningService = provisioningService ?? throw new ArgumentNullException(nameof(provisioningService));
        Providers = providerFactory.Providers;
        InitializeComponent();
        DataContext = this;
        ImapSecurityBox.SelectedItem = MailSecureSocketMode.SslOnConnect;
        SmtpSecurityBox.SelectedItem = MailSecureSocketMode.SslOnConnect;
        ProviderBox.SelectedItem = Providers.FirstOrDefault();
    }

    public IReadOnlyList<MailProviderDescriptor> Providers { get; }
    public IReadOnlyList<MailSecureSocketMode> SecureModes { get; } = Enum.GetValues<MailSecureSocketMode>();
    public MailAccount? ConnectedAccount { get; private set; }

    private MailProviderDescriptor? SelectedProvider => ProviderBox.SelectedItem as MailProviderDescriptor;

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (SelectedProvider is not MailProviderDescriptor provider
            || GmailInformation is null
            || PasswordAccountFields is null
            || GenericSettings is null)
        {
            return;
        }

        bool isGmail = provider.Provider == MailProviderType.Gmail;
        GmailInformation.Visibility = isGmail ? Visibility.Visible : Visibility.Collapsed;
        PasswordAccountFields.Visibility = isGmail ? Visibility.Collapsed : Visibility.Visible;
        GenericSettings.Visibility = provider.Provider == MailProviderType.GenericImap
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectButton.Content = isGmail ? Localizer.Instance.Get("Sign in with Google") : Localizer.Instance.Get("Check and add");
        ConnectButton.IsEnabled = !_isBusy;
        ProviderGuidance.Text = provider.Guidance;
        StatusText.Text = string.Empty;
    }

    private async void Connect_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_isBusy || SelectedProvider is not MailProviderDescriptor provider)
        {
            return;
        }

        MailConnectionSettings? genericSettings = null;
        if (provider.Provider == MailProviderType.GenericImap)
        {
            if (!int.TryParse(ImapPortBox.Text, out int imapPort)
                || !int.TryParse(SmtpPortBox.Text, out int smtpPort))
            {
                StatusText.Text = Localizer.Instance.Get("Enter valid port numbers.");
                return;
            }

            string username = string.IsNullOrWhiteSpace(UsernameBox.Text)
                ? EmailBox.Text
                : UsernameBox.Text;
            genericSettings = new MailConnectionSettings
            {
                Imap = new MailServerSettings
                {
                    Host = ImapHostBox.Text,
                    Port = imapPort,
                    SecureSocketMode = ImapSecurityBox.SelectedItem is MailSecureSocketMode imapMode
                        ? imapMode
                        : MailSecureSocketMode.SslOnConnect,
                    Username = username
                },
                Smtp = new MailServerSettings
                {
                    Host = SmtpHostBox.Text,
                    Port = smtpPort,
                    SecureSocketMode = SmtpSecurityBox.SelectedItem is MailSecureSocketMode smtpMode
                        ? smtpMode
                        : MailSecureSocketMode.SslOnConnect,
                    Username = username
                }
            };
        }

        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        ConnectButton.IsEnabled = false;
        ProviderBox.IsEnabled = false;
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = provider.Provider == MailProviderType.Gmail
            ? Localizer.Instance.Get("Waiting for sign-in in the system browser…")
            : Localizer.Instance.Get("Checking IMAP and SMTP…");
        try
        {
            MailAccountProvisioningResult result = provider.Provider == MailProviderType.Gmail
                ? await _provisioningService.ConnectGmailAsync(_operationCancellation.Token)
                : await _provisioningService.ConnectAsync(
                    new MailAccountConnectionRequest(
                        provider.Provider,
                        EmailBox.Text,
                        DisplayNameBox.Text,
                        genericSettings),
                    SecretBox.Password,
                    _operationCancellation.Token);
            if (!result.IsSuccess)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                StatusText.Text = result.UserMessage;
                return;
            }

            ConnectedAccount = result.Account;
            SecretBox.Clear();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = Localizer.Instance.Get("Connection check canceled.");
        }
        catch
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = Localizer.Instance.Get("Could not complete a secure mail connection.");
        }
        finally
        {
            SecretBox.Clear();
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _isBusy = false;
            ProviderBox.IsEnabled = true;
            ConnectButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        SecretBox.Clear();
        if (_isBusy)
        {
            _operationCancellation?.Cancel();
            StatusText.Text = Localizer.Instance.Get("Canceling connection…");
            return;
        }

        DialogResult = false;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        _operationCancellation?.Cancel();
        base.OnClosed(eventArgs);
    }
}
