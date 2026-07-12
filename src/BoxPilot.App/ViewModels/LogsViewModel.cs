using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class LogsViewModel(AppSessionViewModel session) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    [RelayCommand]
    private void Clear() => Session.ClearLogs();
}
