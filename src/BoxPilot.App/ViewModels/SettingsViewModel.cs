using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public sealed record ThemeChoice(string Code, string DisplayName);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly LocalizationService localization;

    public SettingsViewModel(AppSessionViewModel session, LocalizationService localization)
    {
        Session = session;
        this.localization = localization;
        Languages = localization.Languages;
        Refresh();
    }

    public AppSessionViewModel Session { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

    public IReadOnlyList<ThemeChoice> Themes => themeChoices;

    private readonly List<ThemeChoice> themeChoices = [];

    [ObservableProperty]
    public partial ThemeChoice? Theme { get; set; }

    [ObservableProperty]
    public partial LanguageOption? Language { get; set; }

    [ObservableProperty]
    public partial string CorePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MixedPort { get; set; } = 2080;

    [ObservableProperty]
    public partial int ApiPort { get; set; } = 9090;

    [ObservableProperty]
    public partial bool EnableSystemProxy { get; set; } = true;

    [ObservableProperty]
    public partial bool AllowLan { get; set; }

    [ObservableProperty]
    public partial string CustomDnsServer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EnableTun { get; set; }

    [ObservableProperty]
    public partial bool StartCoreOnLaunch { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = true;

    [RelayCommand]
    private Task SaveAsync()
    {
        var settings = Session.Settings with
        {
            Theme = Theme?.Code ?? "Light",
            Language = Language?.Code ?? "zh-CN",
            SingBoxPath = CorePath.Trim(),
            MixedPort = Math.Clamp(MixedPort, 1, 65_535),
            ClashApiPort = Math.Clamp(ApiPort, 1, 65_535),
            EnableSystemProxy = EnableSystemProxy,
            AllowLan = AllowLan,
            CustomDnsServer = CustomDnsServer.Trim(),
            EnableTun = EnableTun,
            StartCoreOnLaunch = StartCoreOnLaunch,
            CloseToTray = CloseToTray,
        };
        return Session.SaveSettingsAsync(settings);
    }

    [RelayCommand]
    private void OpenDataDirectory() => Session.OpenDataDirectory();

    [RelayCommand]
    private Task UninstallTunServiceAsync() => Session.UninstallTunServiceAsync();

    public void Refresh()
    {
        var settings = Session.Settings;
        RefreshThemes(settings.Theme);
        Language = Languages.FirstOrDefault(item => item.Code == settings.Language) ?? Languages[0];
        CorePath = settings.SingBoxPath;
        MixedPort = settings.MixedPort;
        ApiPort = settings.ClashApiPort;
        EnableSystemProxy = settings.EnableSystemProxy;
        AllowLan = settings.AllowLan;
        CustomDnsServer = settings.CustomDnsServer;
        EnableTun = settings.EnableTun;
        StartCoreOnLaunch = settings.StartCoreOnLaunch;
        CloseToTray = settings.CloseToTray;
    }

    public void NotifyLanguageChanged()
    {
        RefreshThemes(Theme?.Code ?? Session.Settings.Theme);
        OnPropertyChanged(nameof(Languages));
    }

    private void RefreshThemes(string selectedCode)
    {
        themeChoices.Clear();
        themeChoices.AddRange(
        [
            new ThemeChoice("System", localization["System"]),
            new ThemeChoice("Light", localization["Light"]),
            new ThemeChoice("Dark", localization["Dark"]),
        ]);
        Theme = themeChoices.FirstOrDefault(item => item.Code == selectedCode) ?? themeChoices[0];
        OnPropertyChanged(nameof(Themes));
    }
}
