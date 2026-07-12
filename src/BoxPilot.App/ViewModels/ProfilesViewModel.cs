using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ProfilesViewModel(AppSessionViewModel session) : ViewModelBase
{
    public AppSessionViewModel Session { get; } = session;

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
    private async Task RefreshAsync()
    {
        await Session.RefreshSelectedSubscriptionAsync();
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
}
