using System.Collections.ObjectModel;
using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public sealed partial class ProxyGroupItemViewModel : ViewModelBase
{
    public ProxyGroupItemViewModel(
        ProxyChoice choice,
        LocalizationService localization,
        Func<ProxyNodeItemViewModel, Task> select,
        Func<ProxyNodeItemViewModel, Task> test)
    {
        Name = choice.Group;
        Nodes = new ObservableCollection<ProxyNodeItemViewModel>(choice.Options.Select(node =>
            new ProxyNodeItemViewModel(choice.Group, node, localization, select, test)));
        Selected = choice.Selected;
    }

    public string Name { get; }

    public ObservableCollection<ProxyNodeItemViewModel> Nodes { get; }

    [ObservableProperty]
    public partial string Selected { get; private set; }

    public string CountDisplay => $"{Nodes.Count}";

    public void Select(string name)
    {
        Selected = name;
        UpdateSelection();
    }

    partial void OnSelectedChanged(string value)
    {
        UpdateSelection();
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
        LocalizationService localization,
        Func<ProxyNodeItemViewModel, Task> select,
        Func<ProxyNodeItemViewModel, Task> test)
    {
        Group = group;
        Name = node.Name;
        Type = node.Type;
        SupportsUdp = node.SupportsUdp;
        IsGroup = node.IsGroup;
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

    public bool HasFastDelay => !IsTesting && Delay is > 0 and < 300;

    public bool HasMediumDelay => !IsTesting && Delay is >= 300 and < 800;

    public bool HasSlowDelay => !IsTesting && Delay is 0 or >= 800;

    [RelayCommand]
    private Task SelectAsync()
    {
        return select(this);
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

    private void NotifyDelayState()
    {
        OnPropertyChanged(nameof(DelayDisplay));
        OnPropertyChanged(nameof(HasFastDelay));
        OnPropertyChanged(nameof(HasMediumDelay));
        OnPropertyChanged(nameof(HasSlowDelay));
    }
}
