using System.Collections.ObjectModel;
using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public sealed partial class ProxyGroupItemViewModel : ViewModelBase
{
    private readonly LocalizationService localization;

    public ProxyGroupItemViewModel(
        ProxyChoice choice,
        LocalizationService localization,
        Func<ProxyNodeItemViewModel, Task> select,
        Func<ProxyNodeItemViewModel, Task> test)
    {
        Name = choice.Group;
        DisplayName = SingBoxConfigService.IsManagedGlobalSelector(choice.Group)
            ? localization["GlobalMode"]
            : choice.Group;
        IsSelectable = choice.IsSelectable;
        Nodes = new ObservableCollection<ProxyNodeItemViewModel>(choice.Options.Select(node =>
            new ProxyNodeItemViewModel(
                choice.Group,
                node,
                choice.IsSelectable,
                localization,
                select,
                test)));
        Selected = choice.Selected;
        this.localization = localization;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public bool IsSelectable { get; }

    public ObservableCollection<ProxyNodeItemViewModel> Nodes { get; }

    [ObservableProperty]
    public partial string Selected { get; private set; }

    public string CountDisplay => $"{Nodes.Count}";

    public string SelectionDisplay => IsSelectable
        ? Selected
        : string.IsNullOrWhiteSpace(Selected)
            ? localization["AutomaticGroup"]
            : $"{localization["AutomaticGroup"]} · {Selected}";

    public void Select(string name)
    {
        Selected = name;
    }

    partial void OnSelectedChanged(string value)
    {
        UpdateSelection();
        OnPropertyChanged(nameof(SelectionDisplay));
    }

    private void UpdateSelection()
    {
        foreach (var node in Nodes)
        {
            node.IsSelected = string.Equals(
                node.Name,
                Selected,
                StringComparison.Ordinal);
        }
    }
}

public sealed partial class ProxyNodeItemViewModel : ViewModelBase
{
    private readonly LocalizationService localization;
    private readonly Func<ProxyNodeItemViewModel, Task> select;
    private readonly Func<ProxyNodeItemViewModel, Task> test;

    public ProxyNodeItemViewModel(
        string group,
        ProxyNode node,
        bool canSelect,
        LocalizationService localization,
        Func<ProxyNodeItemViewModel, Task> select,
        Func<ProxyNodeItemViewModel, Task> test)
    {
        Group = group;
        Name = node.Name;
        Type = node.Type;
        SupportsUdp = node.SupportsUdp;
        IsGroup = node.IsGroup;
        CanSelect = canSelect;
        Delay = node.Delay;
        this.localization = localization;
        this.select = select;
        this.test = test;
    }

    public string Group { get; }

    public string Name { get; }

    public string Type { get; }

    public bool SupportsUdp { get; }

    public bool IsGroup { get; }

    public bool CanSelect { get; }

    public bool CanApplySelection => CanSelect && !IsApplying;

    public string TypeDisplay => SupportsUdp ? $"{Type} · UDP" : Type;

    [ObservableProperty]
    public partial int? Delay { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    public string DelayDisplay => IsTesting
        ? "…"
        : Delay switch
        {
            null => "—",
            <= 0 => localization["Timeout"],
            _ => $"{Delay} ms",
        };

    public bool HasFastDelay => !IsTesting && Delay is > 0 and < 100;

    public bool HasMediumDelay => !IsTesting && Delay is >= 100 and <= 250;

    public bool HasSlowDelay => !IsTesting && Delay is 0 or > 250;

    [RelayCommand]
    private Task SelectAsync()
    {
        return CanSelect ? select(this) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task TestAsync()
    {
        return test(this);
    }

    partial void OnDelayChanged(int? value)
    {
        NotifyDelayState();
    }

    partial void OnIsTestingChanged(bool value)
    {
        NotifyDelayState();
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplySelection));
    }

    private void NotifyDelayState()
    {
        OnPropertyChanged(nameof(DelayDisplay));
        OnPropertyChanged(nameof(HasFastDelay));
        OnPropertyChanged(nameof(HasMediumDelay));
        OnPropertyChanged(nameof(HasSlowDelay));
    }
}
