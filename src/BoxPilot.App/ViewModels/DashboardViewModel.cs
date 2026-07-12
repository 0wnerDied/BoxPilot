using System.Collections.ObjectModel;
using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class DashboardViewModel(
    AppSessionViewModel session,
    LocalizationService localization) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

    public ObservableCollection<ProxyChoice> ProxyChoices { get; } = [];

    [ObservableProperty]
    public partial ProxyChoice? SelectedChoice { get; set; }

    [ObservableProperty]
    public partial string? SelectedProxy { get; set; }

    [ObservableProperty]
    public partial string DelayText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshingProxies { get; private set; }

    [RelayCommand]
    private Task StartAsync() => Session.StartCoreAsync();

    [RelayCommand]
    private Task StopAsync() => Session.StopCoreAsync();

    [RelayCommand]
    private Task RestartAsync() => Session.RestartCoreAsync();

    [RelayCommand]
    public async Task RefreshProxiesAsync()
    {
        if (!Session.IsCoreRunning || IsRefreshingProxies)
            return;

        IsRefreshingProxies = true;
        try
        {
            using var client = new ClashApiClient(
                Session.Settings.ClashApiPort,
                Session.Settings.ClashApiSecret);
            var choices = await client.GetProxyChoicesAsync();
            ProxyChoices.Clear();
            foreach (var choice in choices)
                ProxyChoices.Add(choice);
            SelectedChoice = ProxyChoices.FirstOrDefault();
            SelectedProxy = SelectedChoice?.Selected;
        }
        catch (Exception exception)
        {
            DelayText = exception.Message;
        }
        finally
        {
            IsRefreshingProxies = false;
        }
    }

    [RelayCommand]
    private async Task ApplyProxyAsync()
    {
        if (SelectedChoice is null || string.IsNullOrWhiteSpace(SelectedProxy))
            return;

        try
        {
            using var client = new ClashApiClient(
                Session.Settings.ClashApiPort,
                Session.Settings.ClashApiSecret);
            await client.SelectProxyAsync(SelectedChoice.Group, SelectedProxy);
            DelayText = "✓";
            await RefreshProxiesAsync();
        }
        catch (Exception exception)
        {
            DelayText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task TestDelayAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProxy))
            return;

        try
        {
            using var client = new ClashApiClient(
                Session.Settings.ClashApiPort,
                Session.Settings.ClashApiSecret);
            var delay = await client.TestDelayAsync(SelectedProxy);
            DelayText = delay is null ? localization["Timeout"] : $"{delay} ms";
        }
        catch (Exception exception)
        {
            DelayText = exception.Message;
        }
    }

    partial void OnSelectedChoiceChanged(ProxyChoice? value)
    {
        SelectedProxy = value?.Selected;
    }
}
