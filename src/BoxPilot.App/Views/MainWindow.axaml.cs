using Avalonia.Controls;
using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;

namespace BoxPilot.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public bool AllowClose { get; set; }

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
