namespace UnifiedMessenger.App.Views;

public partial class WelcomeView : System.Windows.Controls.UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
    }

    private void Support_Click(object sender, System.Windows.RoutedEventArgs eventArgs)
    {
        _ = new UnifiedMessenger.App.Services.WebView.ExternalBrowserService()
            .TryOpen(new Uri("https://t.me/dscripchenko"));
    }
}
