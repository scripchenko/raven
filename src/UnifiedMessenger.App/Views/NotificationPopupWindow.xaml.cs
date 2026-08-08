using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedMessenger.App.Services.Notifications;

namespace UnifiedMessenger.App.Views;

public partial class NotificationPopupWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private bool _completionReported;

    public NotificationPopupWindow(NotificationPopupDisplayModel notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        NotificationId = notification.NotificationId;
        DataContext = notification;
        InitializeComponent();

        _closeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _closeTimer.Tick += OnCloseTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public event EventHandler? PopupClicked;
    public event EventHandler? PopupClosed;

    public Guid NotificationId { get; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        _closeTimer.Start();
    }

    private void OnCloseTimerTick(object? sender, EventArgs eventArgs) => Close();

    private void Popup_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (HasButtonAncestor(eventArgs.OriginalSource as DependencyObject) || _completionReported)
        {
            return;
        }

        _completionReported = true;
        PopupClicked?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _closeTimer.Stop();
        _closeTimer.Tick -= OnCloseTimerTick;
        Closed -= OnClosed;
        if (_completionReported)
        {
            return;
        }

        _completionReported = true;
        PopupClosed?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasButtonAncestor(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
