using System.Windows;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApplySavedWindowSettings(viewModel.WindowSettings);
    }

    protected override void OnClosed(EventArgs e)
    {
        Rect bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        _viewModel.UpdateWindowSettings(
            bounds.Width,
            bounds.Height,
            bounds.Left,
            bounds.Top,
            WindowState == WindowState.Maximized);
        base.OnClosed(e);
    }

    private void ApplySavedWindowSettings(WindowSettings settings)
    {
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);

        if (settings.Left is double left
            && settings.Top is double top
            && IsPositionVisible(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (settings.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsPositionVisible(double left, double top, double width, double height)
    {
        Rect savedBounds = new(left, top, width, height);
        Rect virtualScreen = new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        savedBounds.Intersect(virtualScreen);
        return savedBounds.Width >= 120 && savedBounds.Height >= 80;
    }
}
