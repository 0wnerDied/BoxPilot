using Avalonia.Controls;
using Avalonia.Input;
using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;

namespace BoxPilot.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public bool AllowClose { get; set; }

    private void OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var point = eventArgs.GetPosition(ToastRegion);
        var bounds = ToastRegion.Bounds;
        var isPointerOver = bounds.Width > 0
                            && bounds.Height > 0
                            && point.X >= 0
                            && point.X <= bounds.Width
                            && point.Y >= 0
                            && point.Y <= bounds.Height;
        viewModel.Session.Toast.SetPointerOver(isPointerOver);
    }

    private void OnPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.Session.Toast.SetPointerOver(false);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (AllowClose)
            return;

        if (DataContext is MainViewModel { Session.Settings.CloseToTray: true })
        {
            eventArgs.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            MacOSDockService.SetDockVisible(false);
            return;
        }

        // OnLastWindowClose handles normal shutdown when close-to-tray is disabled.
    }
}
