using Avalonia.Controls;
using BoxPilot.App.Services;
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
        MacOSDockService.SetDockVisible(true);
        window.ShowInTaskbar = true;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        ShowWindow();
        var dialog = new AboutWindow();
        await dialog.ShowDialog(window);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ShowWindow();
        if (window.DataContext is MainViewModel viewModel)
            viewModel.NavigateCommand.Execute("settings");
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
