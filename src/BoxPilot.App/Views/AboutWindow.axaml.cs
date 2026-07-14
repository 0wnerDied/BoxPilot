using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BoxPilot.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionValue.Text = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3)
            ?? "1.0.3";
    }

    private void CloseClick(object? sender, RoutedEventArgs eventArgs)
    {
        Close();
    }
}
