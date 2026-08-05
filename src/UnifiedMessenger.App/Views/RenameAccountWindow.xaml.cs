using System.Windows;
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
                "Название аккаунта должно содержать от 1 до 80 символов.",
                "Некорректное название",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AccountName = accountName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
