using BoxPilot.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ProfilesViewModel(
    AppSessionViewModel session,
    LocalizationService localization) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

    public string ImportConfigurationTitle => localization["ImportConfiguration"];

    public string ExportConfigurationTitle => localization["ExportConfiguration"];

    public string ExportFileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var name = Session.SelectedProfile?.Name ?? "sing-box";
            return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
                   + ".json";
        }
    }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SubscriptionUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDeleteConfirmationVisible { get; private set; }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var outcome = await Session.ImportSubscriptionAsync(ProfileName, SubscriptionUrl);
        if (outcome is null)
            return;

        SubscriptionUrl = string.Empty;
    }

    [RelayCommand]
    private Task RefreshAsync() => Session.RefreshSelectedSubscriptionAsync();

    [RelayCommand]
    private Task CreateBlankAsync()
    {
        return Session.CreateBlankProfileAsync(ProfileName);
    }

    public Task ImportConfigurationAsync(IReadOnlyList<string> paths)
    {
        return Session.ImportConfigurationFileAsync(ProfileName, paths);
    }

    public Task ExportConfigurationAsync(string path)
    {
        return Session.ExportSelectedConfigurationAsync(path);
    }

    [RelayCommand]
    private void BeginDelete()
    {
        IsDeleteConfirmationVisible = Session.SelectedProfile is not null;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmationVisible = false;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        await Session.DeleteSelectedProfileAsync();
        IsDeleteConfirmationVisible = false;
    }

    [RelayCommand]
    private Task AddRoutingModesAsync() => Session.EnableStandardRoutingModesAsync();

    [RelayCommand]
    private Task KeepSubscriptionRoutingAsync() => Session.KeepSubscriptionRoutingAsync();
}
