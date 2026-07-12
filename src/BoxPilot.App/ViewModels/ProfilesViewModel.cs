using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly LocalizationService localization;

    public ProfilesViewModel(AppSessionViewModel session, LocalizationService localization)
    {
        Session = session;
        this.localization = localization;
        ProfileName = localization["ManualProfile"];
    }

    public AppSessionViewModel Session { get; }

    [ObservableProperty]
    public partial string ProfileName { get; set; }

    [ObservableProperty]
    public partial string SubscriptionUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDeleteConfirmationVisible { get; private set; }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var outcome = await Session.ImportSubscriptionAsync(ProfileName, SubscriptionUrl);
        if (outcome is null)
            return;

        ImportDetails = string.Join(Environment.NewLine, outcome.Warnings);
        SubscriptionUrl = string.Empty;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var outcome = await Session.RefreshSelectedSubscriptionAsync();
        if (outcome is not null)
            ImportDetails = string.Join(Environment.NewLine, outcome.Warnings);
    }

    [RelayCommand]
    private Task CreateBlankAsync()
    {
        return Session.CreateBlankProfileAsync(ProfileName);
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

    public void NotifyLanguageChanged()
    {
        if (string.IsNullOrWhiteSpace(ProfileName)
            || ProfileName is "Manual profile" or "手动配置")
        {
            ProfileName = localization["ManualProfile"];
        }
    }
}
