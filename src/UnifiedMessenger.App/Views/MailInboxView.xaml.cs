namespace UnifiedMessenger.App.Views;

public partial class MailInboxView : System.Windows.Controls.UserControl
{
    public event EventHandler? ShowRemoteImagesRequested;

    public MailInboxView()
    {
        InitializeComponent();
    }

    internal System.Windows.FrameworkElement HtmlRendererSurface => MailHtmlRendererSurface;

    private void ShowRemoteImages_Click(object sender, System.Windows.RoutedEventArgs eventArgs) =>
        ShowRemoteImagesRequested?.Invoke(this, EventArgs.Empty);
}
