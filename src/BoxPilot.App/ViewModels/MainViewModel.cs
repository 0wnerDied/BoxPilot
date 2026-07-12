using BoxPilot.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly LocalizationService localization;

    public MainViewModel(AppSessionViewModel session, LocalizationService localization)
    {
        Session = session;
        this.localization = localization;

        Dashboard = new DashboardViewModel(session, localization);
        Profiles = new ProfilesViewModel(session, localization);
        Configuration = new ConfigurationViewModel(session);
        Logs = new LogsViewModel(session);
        Tools = new ToolsViewModel(session, localization);
        Settings = new SettingsViewModel(session, localization);
        CurrentPage = Dashboard;
        CurrentPageTitle = localization["NavDashboard"];
        localization.LanguageChanged += OnLanguageChanged;
    }

    public AppSessionViewModel Session { get; }

    public DashboardViewModel Dashboard { get; }

    public ProfilesViewModel Profiles { get; }

    public ConfigurationViewModel Configuration { get; }

    public LogsViewModel Logs { get; }

    public ToolsViewModel Tools { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; private set; }

    [ObservableProperty]
    public partial string CurrentPageTitle { get; private set; }

    [ObservableProperty]
    public partial string CurrentPageKey { get; private set; } = "dashboard";

    public bool IsDashboardSelected => CurrentPageKey == "dashboard";

    public bool IsProfilesSelected => CurrentPageKey == "profiles";

    public bool IsConfigurationSelected => CurrentPageKey == "configuration";

    public bool IsLogsSelected => CurrentPageKey == "logs";

    public bool IsToolsSelected => CurrentPageKey == "tools";

    public bool IsSettingsSelected => CurrentPageKey == "settings";

    [RelayCommand]
    private void Navigate(string? page)
    {
        CurrentPageKey = page?.ToLowerInvariant() ?? "dashboard";
        var destination = CurrentPageKey switch
        {
            "profiles" => ((ViewModelBase)Profiles, localization["NavProfiles"]),
            "configuration" => (Configuration, localization["NavConfiguration"]),
            "logs" => (Logs, localization["NavLogs"]),
            "tools" => (Tools, localization["NavTools"]),
            "settings" => (Settings, localization["NavSettings"]),
            _ => (Dashboard, localization["NavDashboard"]),
        };
        CurrentPage = destination.Item1;
        CurrentPageTitle = destination.Item2;

        if (CurrentPage == Settings)
            Settings.Refresh();
        if (CurrentPage == Dashboard && Session.IsCoreRunning)
            _ = Dashboard.RefreshProxiesAsync();
    }

    private void OnLanguageChanged()
    {
        Navigate(CurrentPageKey);
        Profiles.NotifyLanguageChanged();
        Settings.NotifyLanguageChanged();
    }

    partial void OnCurrentPageKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsProfilesSelected));
        OnPropertyChanged(nameof(IsConfigurationSelected));
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }
}
