namespace UnifiedMessenger.App.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void AddMailAccount_Click(object sender, System.Windows.RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.SettingsViewModel viewModel)
        {
            viewModel.RequestAddMailAccount();
        }
    }
}
