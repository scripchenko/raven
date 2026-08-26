namespace UnifiedMessenger.App.Views;

public partial class MailInboxView : System.Windows.Controls.UserControl
{
    public event EventHandler? ShowRemoteImagesRequested;
    public event EventHandler? AlwaysShowRemoteImagesFromSenderRequested;
    public event EventHandler? RevokeRemoteImagesFromSenderRequested;
    public event EventHandler? PrintRequested;

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

    private void AlwaysShowRemoteImagesFromSender_Click(
        object sender,
        System.Windows.RoutedEventArgs eventArgs) =>
        AlwaysShowRemoteImagesFromSenderRequested?.Invoke(this, EventArgs.Empty);

    private void RevokeRemoteImagesFromSender_Click(
        object sender,
        System.Windows.RoutedEventArgs eventArgs) =>
        RevokeRemoteImagesFromSenderRequested?.Invoke(this, EventArgs.Empty);

    private void Print_Click(object sender, System.Windows.RoutedEventArgs eventArgs) =>
        PrintRequested?.Invoke(this, EventArgs.Empty);
}
