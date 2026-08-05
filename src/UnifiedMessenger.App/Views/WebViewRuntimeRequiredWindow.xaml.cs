using System.Windows;

namespace UnifiedMessenger.App.Views;

public partial class WebViewRuntimeRequiredWindow : Window
{
    public WebViewRuntimeRequiredWindow()
    {
        InitializeComponent();
    }

    private void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
