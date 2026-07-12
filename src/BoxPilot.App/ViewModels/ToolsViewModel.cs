using BoxPilot.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ToolsViewModel(
    AppSessionViewModel session,
    LocalizationService localization) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

    [ObservableProperty]
    public partial string CommandText { get; set; } = "version";

    [ObservableProperty]
    public partial string Output { get; private set; } = string.Empty;

    [RelayCommand]
    private async Task RunAsync()
    {
        try
        {
            var result = await Session.RunToolAsync(CommandText);
            Output = string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? string.Format(localization["ProcessExited"], result.ExitCode)
                : result.CombinedOutput;
        }
        catch (Exception exception)
        {
            Output = exception.Message;
        }
    }

    [RelayCommand]
    private void UseCommand(string? command)
    {
        if (!string.IsNullOrWhiteSpace(command))
            CommandText = command;
    }
}
