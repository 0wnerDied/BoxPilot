using System.Collections.Specialized;
using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class ConfigurationViewModel : ViewModelBase
{
    private readonly LocalizationService localization;

    public ConfigurationViewModel(
        AppSessionViewModel session,
        LocalizationService localization)
    {
        Session = session;
        this.localization = localization;
        Session.RoutingOutbounds.CollectionChanged += OnRoutingOutboundsChanged;
        SelectAvailableOutbound();
    }

    public AppSessionViewModel Session { get; }

    public string RuleSetPickerTitle => localization["ImportLocalRuleSet"];

    [ObservableProperty]
    public partial RoutingOutbound? SelectedRoutingOutbound { get; set; }

    [ObservableProperty]
    public partial string RemoteRuleSetUrl { get; set; } = string.Empty;

    [RelayCommand]
    private Task SaveAsync() => Session.SaveConfigurationAsync();

    [RelayCommand]
    private Task FormatAsync() => Session.FormatConfigurationAsync();

    [RelayCommand]
    private Task ValidateAsync() => Session.ValidateConfigurationAsync();

    public Task ImportLocalRuleSetAsync(string path)
    {
        var outbound = ResolveOutbound();
        return Session.ImportRuleSetAsync(path, outbound.Tag);
    }

    [RelayCommand]
    private async Task AddRemoteRuleSetAsync()
    {
        var outbound = ResolveOutbound();
        await Session.AddRemoteRuleSetAsync(RemoteRuleSetUrl, outbound.Tag);
        RemoteRuleSetUrl = string.Empty;
    }

    public Task RemoveRuleSetAsync(CustomRuleSet ruleSet)
    {
        return Session.RemoveRuleSetAsync(ruleSet);
    }

    private RoutingOutbound ResolveOutbound()
    {
        SelectAvailableOutbound();
        return SelectedRoutingOutbound
               ?? throw new InvalidOperationException(localization["NoRouteTarget"]);
    }

    private void OnRoutingOutboundsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        SelectAvailableOutbound();
    }

    private void SelectAvailableOutbound()
    {
        SelectedRoutingOutbound = Session.RoutingOutbounds.FirstOrDefault(outbound => string.Equals(
                                      outbound.Tag,
                                      SelectedRoutingOutbound?.Tag,
                                      StringComparison.Ordinal))
                                  ?? Session.RoutingOutbounds.FirstOrDefault();
    }
}
