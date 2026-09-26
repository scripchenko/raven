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

    private async void Language_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.SettingsViewModel viewModel
            && sender is System.Windows.Controls.ComboBox { SelectedValue: string language }
            && language != viewModel.Language)
        {
            await viewModel.SetLanguageCommand.ExecuteAsync(language);
        }
    }
}
