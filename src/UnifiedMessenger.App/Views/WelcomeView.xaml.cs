namespace UnifiedMessenger.App.Views;

public partial class WelcomeView : System.Windows.Controls.UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
    }

    private void Support_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs eventArgs)
    {
        _ = new UnifiedMessenger.App.Services.WebView.ExternalBrowserService()
            .TryOpen(eventArgs.Uri);
        eventArgs.Handled = true;
    }
}
