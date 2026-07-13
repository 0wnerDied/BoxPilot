using System.ComponentModel;
using BoxPilot.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel dashboard;
    private readonly ProfilesViewModel profiles;
    private readonly ConfigurationViewModel configuration;
    private readonly LogsViewModel logs;
    private readonly ToolsViewModel tools;
    private readonly SettingsViewModel settings;
    private string currentPageKey = "dashboard";

    public MainViewModel(AppSessionViewModel session, LocalizationService localization)
    {
        Session = session;

        dashboard = new DashboardViewModel(session, localization);
        profiles = new ProfilesViewModel(session);
        configuration = new ConfigurationViewModel(session, localization);
        logs = new LogsViewModel(session);
        tools = new ToolsViewModel(session, localization);
        settings = new SettingsViewModel(session, localization);
        CurrentPage = dashboard;
        localization.LanguageChanged += OnLanguageChanged;
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    public AppSessionViewModel Session { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; private set; }

    public bool IsDashboardSelected => currentPageKey == "dashboard";

    public bool IsProfilesSelected => currentPageKey == "profiles";

    public bool IsConfigurationSelected => currentPageKey == "configuration";

    public bool IsLogsSelected => currentPageKey == "logs";

    public bool IsToolsSelected => currentPageKey == "tools";

    public bool IsSettingsSelected => currentPageKey == "settings";

    [RelayCommand]
    private void Navigate(string? page)
    {
        (string Key, ViewModelBase Page) destination = page?.ToLowerInvariant() switch
        {
            "profiles" => ("profiles", profiles),
            "configuration" => ("configuration", configuration),
            "logs" => ("logs", logs),
            "tools" => ("tools", tools),
            "settings" => ("settings", settings),
            _ => ("dashboard", dashboard),
        };
        currentPageKey = destination.Key;
        CurrentPage = destination.Page;
        NotifyNavigationState();

        if (CurrentPage == settings)
            settings.Refresh();
        if (CurrentPage == dashboard && Session.IsCoreRunning)
            _ = dashboard.RefreshProxiesAsync();
    }

    private void OnLanguageChanged()
    {
        settings.NotifyLanguageChanged();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(AppSessionViewModel.Settings))
            dashboard.NotifyRoutingModeChanged();
        if (eventArgs.PropertyName != nameof(AppSessionViewModel.IsCoreRunning))
            return;

        if (Session.IsCoreRunning)
            _ = dashboard.RefreshProxiesAsync();
        else
            dashboard.ClearProxies();
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsProfilesSelected));
        OnPropertyChanged(nameof(IsConfigurationSelected));
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }
}
