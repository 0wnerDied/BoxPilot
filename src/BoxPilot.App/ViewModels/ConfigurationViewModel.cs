using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ConfigurationViewModel(AppSessionViewModel session) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

    [RelayCommand]
    private Task SaveAsync() => Session.SaveConfigurationAsync();

    [RelayCommand]
    private Task FormatAsync() => Session.FormatConfigurationAsync();

    [RelayCommand]
    private Task ValidateAsync() => Session.ValidateConfigurationAsync();
}
