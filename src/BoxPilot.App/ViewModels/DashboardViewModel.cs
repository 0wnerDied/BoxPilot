using System.Collections.ObjectModel;
using Avalonia.Threading;
using BoxPilot.App.Services;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoxPilot.App.ViewModels;

public partial class DashboardViewModel(
    AppSessionViewModel session,
    LocalizationService localization) : ViewModelBase
{
    private const int DelayTestConcurrency = 12;
    private const int DelayTestTimeoutMilliseconds = 8_000;
    private bool isRefreshingProxies;
    private bool isTestingGroup;
    private bool isSwitchingNode;
    private IReadOnlyList<ProxyChoice> allProxyChoices = [];

    public AppSessionViewModel Session { get; } = session;

    public ObservableCollection<ProxyGroupItemViewModel> ProxyGroups { get; } = [];

    public ObservableCollection<ProxyNodeItemViewModel> VisibleNodes { get; } = [];

    [ObservableProperty]
    public partial ProxyGroupItemViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SortByLatency { get; set; }

    public bool HasProxyGroups => ProxyGroups.Count > 0;

    public bool HasVisibleNodes => VisibleNodes.Count > 0;

    public bool IsRuleMode => string.Equals(Session.Settings.RoutingMode, "Rule", StringComparison.OrdinalIgnoreCase);

    public bool IsGlobalMode => string.Equals(Session.Settings.RoutingMode, "Global", StringComparison.OrdinalIgnoreCase);

    public bool IsDirectMode => string.Equals(Session.Settings.RoutingMode, "Direct", StringComparison.OrdinalIgnoreCase);

    public bool ShowProxySelection => !IsDirectMode;

    public string GroupCountDisplay => $"{ProxyGroups.Count}";

    public string NodeCountDisplay
    {
        get
        {
            var total = SelectedGroup?.Nodes.Count ?? 0;
            return string.IsNullOrWhiteSpace(SearchText)
                ? $"{total}"
                : $"{VisibleNodes.Count}/{total}";
        }
    }

    [RelayCommand]
    private Task StartAsync() => Session.StartCoreAsync();

    [RelayCommand]
    private Task StopAsync() => Session.StopCoreAsync();

    [RelayCommand]
    private Task RestartAsync() => Session.RestartCoreAsync();

    [RelayCommand]
    private async Task SetRoutingModeAsync(string mode)
    {
        await Session.SetRoutingModeAsync(mode);
        if (Session.IsCoreRunning && ShowProxySelection && allProxyChoices.Count == 0)
            await RefreshProxiesAsync();
    }

    [RelayCommand]
    public async Task RefreshProxiesAsync()
    {
        if (!Session.IsCoreRunning || isRefreshingProxies)
            return;

        var previousGroup = SelectedGroup?.Name;
        isRefreshingProxies = true;
        try
        {
            using var client = CreateApiClient();
            var choices = await LoadProxyChoicesAsync(client);
            allProxyChoices = choices;
            RebuildProxyGroups(previousGroup);
        }
        catch (Exception exception)
        {
            Session.Toast.Show(exception.Message, ToastLevel.Error);
        }
        finally
        {
            isRefreshingProxies = false;
        }
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (SelectedGroup is null || isTestingGroup || !Session.IsCoreRunning)
            return;

        var nodes = SelectedGroup.Nodes.Where(static node => !node.IsGroup).ToArray();
        if (nodes.Length == 0)
            return;

        isTestingGroup = true;
        foreach (var node in nodes)
            node.IsTesting = true;

        var succeeded = 0;
        using var client = CreateApiClient();
        using var gate = new SemaphoreSlim(DelayTestConcurrency, DelayTestConcurrency);
        try
        {
            await Task.WhenAll(nodes.Select(async node =>
            {
                await gate.WaitAsync();
                try
                {
                    var delay = await client.TestDelayAsync(
                            node.Name,
                            timeoutMilliseconds: DelayTestTimeoutMilliseconds)
                        .ConfigureAwait(false);
                    if (delay is > 0)
                        Interlocked.Increment(ref succeeded);
                    await Dispatcher.UIThread.InvokeAsync(() => node.Delay = delay ?? 0);
                }
                catch
                {
                    await Dispatcher.UIThread.InvokeAsync(() => node.Delay = 0);
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() => node.IsTesting = false);
                    gate.Release();
                }
            }));

            if (SortByLatency)
                RebuildVisibleNodes();
            Session.Toast.Show(
                string.Format(localization["DelayTestComplete"], succeeded, nodes.Length),
                succeeded > 0 ? ToastLevel.Success : ToastLevel.Warning);
        }
        finally
        {
            foreach (var node in nodes)
                node.IsTesting = false;
            isTestingGroup = false;
        }
    }

    partial void OnSelectedGroupChanged(ProxyGroupItemViewModel? value)
    {
        RebuildVisibleNodes();
        OnPropertyChanged(nameof(NodeCountDisplay));
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleNodes();
    }

    partial void OnSortByLatencyChanged(bool value)
    {
        RebuildVisibleNodes();
    }

    private async Task SelectNodeAsync(ProxyNodeItemViewModel node)
    {
        if (!Session.IsCoreRunning || isSwitchingNode || node.IsSelected || !node.CanSelect)
            return;

        var group = ProxyGroups.FirstOrDefault(item => string.Equals(
            item.Name,
            node.Group,
            StringComparison.Ordinal));
        if (group is null || !group.IsSelectable)
            return;

        isSwitchingNode = true;
        node.IsApplying = true;
        try
        {
            using var client = CreateApiClient();
            await client.SelectProxyAsync(group.Name, node.Name);
            group.Select(node.Name);
            allProxyChoices = allProxyChoices
                .Select(choice => string.Equals(choice.Group, group.Name, StringComparison.Ordinal)
                    ? choice with { Selected = node.Name }
                    : choice)
                .ToArray();
            Session.Toast.Show(
                $"{localization["NodeSelected"]}: {node.Name}",
                ToastLevel.Success);
        }
        catch (Exception exception)
        {
            Session.Toast.Show(exception.Message, ToastLevel.Error);
        }
        finally
        {
            node.IsApplying = false;
            isSwitchingNode = false;
        }
    }

    private async Task TestNodeAsync(ProxyNodeItemViewModel node)
    {
        if (!Session.IsCoreRunning || node.IsTesting)
            return;

        node.IsTesting = true;
        try
        {
            using var client = CreateApiClient();
            node.Delay = await client.TestDelayAsync(
                    node.Name,
                    timeoutMilliseconds: DelayTestTimeoutMilliseconds)
                ?? 0;
            if (SortByLatency)
                RebuildVisibleNodes();
        }
        catch (Exception exception)
        {
            node.Delay = 0;
            Session.Toast.Show(exception.Message, ToastLevel.Error);
        }
        finally
        {
            node.IsTesting = false;
        }
    }

    private ClashApiClient CreateApiClient()
    {
        return Session.CreateClashApiClient();
    }

    private static async Task<IReadOnlyList<ProxyChoice>> LoadProxyChoicesAsync(
        ClashApiClient client)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.GetProxyChoicesAsync();
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)));
            }
        }
    }

    private void RebuildVisibleNodes()
    {
        var nodes = SelectedGroup?.Nodes.AsEnumerable() ?? [];
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            nodes = nodes.Where(node => node.Name.Contains(
                query,
                StringComparison.OrdinalIgnoreCase));
        }

        if (SortByLatency)
        {
            nodes = nodes
                .OrderBy(static node => node.Delay is > 0 ? node.Delay : int.MaxValue)
                .ThenBy(static node => node.Name, StringComparer.OrdinalIgnoreCase);
        }

        VisibleNodes.Clear();
        foreach (var node in nodes)
            VisibleNodes.Add(node);
        OnPropertyChanged(nameof(HasVisibleNodes));
        OnPropertyChanged(nameof(NodeCountDisplay));
    }

    public void ClearProxies()
    {
        allProxyChoices = [];
        SelectedGroup = null;
        ProxyGroups.Clear();
        VisibleNodes.Clear();
        NotifyProxyCounts();
    }

    public void NotifyRoutingModeChanged()
    {
        OnPropertyChanged(nameof(IsRuleMode));
        OnPropertyChanged(nameof(IsGlobalMode));
        OnPropertyChanged(nameof(IsDirectMode));
        OnPropertyChanged(nameof(ShowProxySelection));
        RebuildProxyGroups(SelectedGroup?.Name);
    }

    private void RebuildProxyGroups(string? previousGroup)
    {
        IEnumerable<ProxyChoice> choices = allProxyChoices;
        if (IsDirectMode)
        {
            choices = [];
        }
        else if (IsGlobalMode)
        {
            choices = string.IsNullOrWhiteSpace(Session.GlobalProxyGroup)
                ? []
                : choices.Where(choice => string.Equals(
                    choice.Group,
                    Session.GlobalProxyGroup,
                    StringComparison.Ordinal));
        }
        else
        {
            choices = choices.Where(static choice =>
                !SingBoxConfigService.IsManagedGlobalSelector(choice.Group));
        }

        ProxyGroups.Clear();
        foreach (var choice in choices)
        {
            ProxyGroups.Add(new ProxyGroupItemViewModel(
                choice,
                localization,
                SelectNodeAsync,
                TestNodeAsync));
        }

        SelectedGroup = ProxyGroups.FirstOrDefault(group => string.Equals(
                            group.Name,
                            previousGroup,
                            StringComparison.Ordinal))
                        ?? ProxyGroups.FirstOrDefault();
        NotifyProxyCounts();
    }

    private void NotifyProxyCounts()
    {
        OnPropertyChanged(nameof(HasProxyGroups));
        OnPropertyChanged(nameof(HasVisibleNodes));
        OnPropertyChanged(nameof(GroupCountDisplay));
        OnPropertyChanged(nameof(NodeCountDisplay));
    }
}
