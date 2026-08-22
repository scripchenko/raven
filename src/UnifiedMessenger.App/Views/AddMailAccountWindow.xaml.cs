using System.Windows;
using System.Windows.Controls;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.Views;

public partial class AddMailAccountWindow : Window
{
    private readonly IMailAccountProvisioningService _provisioningService;

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
        ConnectButton.IsEnabled = !isGmail;
        ProviderGuidance.Text = provider.Guidance;
        StatusText.Text = string.Empty;
    }

    private async void Connect_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedProvider is not MailProviderDescriptor provider || provider.Provider == MailProviderType.Gmail)
        {
            return;
        }

        MailConnectionSettings? genericSettings = null;
        if (provider.Provider == MailProviderType.GenericImap)
        {
            if (!int.TryParse(ImapPortBox.Text, out int imapPort)
                || !int.TryParse(SmtpPortBox.Text, out int smtpPort))
            {
                StatusText.Text = "Укажите корректные номера портов.";
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

        ConnectButton.IsEnabled = false;
        ProviderBox.IsEnabled = false;
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = "Проверяем IMAP и SMTP…";
        try
        {
            MailAccountProvisioningResult result = await _provisioningService.ConnectAsync(
                new MailAccountConnectionRequest(
                    provider.Provider,
                    EmailBox.Text,
                    DisplayNameBox.Text,
                    genericSettings),
                SecretBox.Password);
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
            StatusText.Text = "Проверка подключения отменена.";
        }
        catch
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "Не удалось завершить безопасное подключение почты.";
        }
        finally
        {
            SecretBox.Clear();
            ProviderBox.IsEnabled = true;
            ConnectButton.IsEnabled = SelectedProvider?.Provider != MailProviderType.Gmail;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        SecretBox.Clear();
        DialogResult = false;
    }
}
