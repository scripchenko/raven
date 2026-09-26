using System.Windows;
using UnifiedMessenger.App.Services.Localization;
using WpfMessageBox = System.Windows.MessageBox;

namespace UnifiedMessenger.App.Views;

public partial class RenameAccountWindow : Window
{
    public RenameAccountWindow(string currentName)
    {
        InitializeComponent();
        AccountNameTextBox.Text = currentName;
        AccountNameTextBox.SelectAll();
        Loaded += (_, _) => AccountNameTextBox.Focus();
    }

    public string AccountName { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        string accountName = AccountNameTextBox.Text.Trim();
        if (accountName.Length is 0 or > 80)
        {
            WpfMessageBox.Show(
                this,
                Localizer.Instance.Get("Account name must be 1 to 80 characters."),
                Localizer.Instance.Get("Invalid name"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AccountName = accountName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
