using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class WpfTaskbarActivityIndicator : ITaskbarActivityIndicator
{
    private static readonly ImageSource ActivityOverlay = CreateActivityOverlay();
    private Window? _window;

    public bool HasActivity { get; private set; }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
        ApplyOverlay();
    }

    public void Detach(Window window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }
    }

    public void SetHasActivity(bool hasActivity)
    {
        HasActivity = hasActivity;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        if (_window?.TaskbarItemInfo is TaskbarItemInfo taskbarItem)
        {
            taskbarItem.Overlay = HasActivity ? ActivityOverlay : null;
        }
    }

    private static ImageSource CreateActivityOverlay()
    {
        GeometryDrawing drawing = new(
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 57, 53)),
            new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.5),
            new EllipseGeometry(new System.Windows.Point(8, 8), 6.5, 6.5));
        DrawingImage image = new(drawing);
        image.Freeze();
        return image;
    }
}
