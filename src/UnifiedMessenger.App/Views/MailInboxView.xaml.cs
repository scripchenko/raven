namespace UnifiedMessenger.App.Views;

public partial class MailInboxView : System.Windows.Controls.UserControl
{
    public event EventHandler? ShowRemoteImagesRequested;

    public MailInboxView()
    {
        InitializeComponent();
    }

    internal System.Windows.FrameworkElement HtmlRendererSurface => MailHtmlRendererSurface;

    private void ComposeSurface_IsVisibleChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is not true)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (ComposeToTextBox.IsVisible && ComposeToTextBox.IsEnabled)
                {
                    ComposeToTextBox.Focus();
                    System.Windows.Input.Keyboard.Focus(ComposeToTextBox);
                }
            });
    }

    private void ShowRemoteImages_Click(object sender, System.Windows.RoutedEventArgs eventArgs) =>
        ShowRemoteImagesRequested?.Invoke(this, EventArgs.Empty);
}
