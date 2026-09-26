using System.Windows;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Localization;

namespace UnifiedMessenger.App.Views;

public partial class NotificationPermissionWindow : Window
{
    public NotificationPermissionWindow(string accountDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDisplayName);
        InitializeComponent();
        PromptText.Text = Localizer.Instance.Format("{0} is requesting permission to show notifications", accountDisplayName);
    }

    private void Allow_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = true;
    private void Deny_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}

public sealed class WpfNotificationPermissionPrompt : INotificationPermissionPrompt
{
    public bool Show(string accountDisplayName)
    {
        NotificationPermissionWindow dialog = new(accountDisplayName)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true;
    }
}
