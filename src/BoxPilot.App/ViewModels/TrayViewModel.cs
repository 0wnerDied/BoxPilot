using Avalonia.Controls;
using BoxPilot.App.Views;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class TrayViewModel(
    AppSessionViewModel session,
    MainWindow window) : ViewModelBase
{
    [RelayCommand]
    private void ShowWindow()
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    [RelayCommand]
    private Task StartAsync() => session.StartCoreAsync();

    [RelayCommand]
    private Task StopAsync() => session.StopCoreAsync();

    [RelayCommand]
    private void Quit()
    {
        window.AllowClose = true;
        window.Close();
    }
}
