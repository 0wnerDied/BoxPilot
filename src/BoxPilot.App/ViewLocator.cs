using Avalonia.Controls;
using Avalonia.Controls.Templates;
using BoxPilot.App.ViewModels;
using BoxPilot.App.Views;

namespace BoxPilot.App;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? parameter)
    {
        return parameter switch
        {
            ConfigurationViewModel => new ConfigurationView(),
            DashboardViewModel => new DashboardView(),
            LogsViewModel => new LogsView(),
            ProfilesViewModel => new ProfilesView(),
            SettingsViewModel => new SettingsView(),
            ToolsViewModel => new ToolsView(),
            _ => null,
        };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
