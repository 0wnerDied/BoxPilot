using System.ComponentModel;
using BoxPilot.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel(AppSessionViewModel session, LocalizationService localization)
    {
        Session = session;

        Dashboard = new DashboardViewModel(session, localization);
        Profiles = new ProfilesViewModel(session);
        Configuration = new ConfigurationViewModel(session);
        Logs = new LogsViewModel(session);
        Tools = new ToolsViewModel(session, localization);
        Settings = new SettingsViewModel(session, localization);
        CurrentPage = Dashboard;
        localization.LanguageChanged += OnLanguageChanged;
        session.PropertyChanged += OnSessionPropertyChanged;
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
        CurrentPage = CurrentPageKey switch
        {
            "profiles" => Profiles,
            "configuration" => Configuration,
            "logs" => Logs,
            "tools" => Tools,
            "settings" => Settings,
            _ => Dashboard,
        };

        if (CurrentPage == Settings)
            Settings.Refresh();
        if (CurrentPage == Dashboard && Session.IsCoreRunning)
            _ = Dashboard.RefreshProxiesAsync();
    }

    private void OnLanguageChanged()
    {
        Settings.NotifyLanguageChanged();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(AppSessionViewModel.IsCoreRunning))
            return;

        if (Session.IsCoreRunning)
            _ = Dashboard.RefreshProxiesAsync();
        else
            Dashboard.ClearProxies();
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
