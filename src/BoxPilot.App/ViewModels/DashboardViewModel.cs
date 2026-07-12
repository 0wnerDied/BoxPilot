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

    public AppSessionViewModel Session { get; } = session;

    public ObservableCollection<ProxyGroupItemViewModel> ProxyGroups { get; } = [];

    public ObservableCollection<ProxyNodeItemViewModel> VisibleNodes { get; } = [];

    [ObservableProperty]
    public partial ProxyGroupItemViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SortByLatency { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshingProxies { get; private set; }

    [ObservableProperty]
    public partial bool IsTestingGroup { get; private set; }

    [ObservableProperty]
    public partial bool IsSwitchingNode { get; private set; }

    public bool HasProxyGroups => ProxyGroups.Count > 0;

    public bool HasVisibleNodes => VisibleNodes.Count > 0;

    public string SelectedNodeDisplay => SelectedGroup?.Selected ?? "—";

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
    private async Task StartAsync()
    {
        await Session.StartCoreAsync();
        await RefreshWhenRunningAsync();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await Session.StopCoreAsync();
        ClearProxies();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        await Session.RestartCoreAsync();
        await RefreshWhenRunningAsync();
    }

    [RelayCommand]
    public async Task RefreshProxiesAsync()
    {
        if (!Session.IsCoreRunning || IsRefreshingProxies)
            return;

        var previousGroup = SelectedGroup?.Name;
        IsRefreshingProxies = true;
        try
        {
            using var client = CreateApiClient();
            var choices = await LoadProxyChoicesAsync(client);
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
        catch (Exception exception)
        {
            Session.Toast.Show(exception.Message, ToastLevel.Error);
        }
        finally
        {
            IsRefreshingProxies = false;
        }
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (SelectedGroup is null || IsTestingGroup || !Session.IsCoreRunning)
            return;

        var nodes = SelectedGroup.Nodes.Where(static node => !node.IsGroup).ToArray();
        if (nodes.Length == 0)
            return;

        IsTestingGroup = true;
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
            IsTestingGroup = false;
        }
    }

    partial void OnSelectedGroupChanged(ProxyGroupItemViewModel? value)
    {
        RebuildVisibleNodes();
        OnPropertyChanged(nameof(SelectedNodeDisplay));
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
        if (!Session.IsCoreRunning || IsSwitchingNode || node.IsSelected)
            return;

        var group = ProxyGroups.FirstOrDefault(item => string.Equals(
            item.Name,
            node.Group,
            StringComparison.Ordinal));
        if (group is null)
            return;

        IsSwitchingNode = true;
        node.IsApplying = true;
        try
        {
            using var client = CreateApiClient();
            await client.SelectProxyAsync(group.Name, node.Name);
            group.Select(node.Name);
            OnPropertyChanged(nameof(SelectedNodeDisplay));
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
            IsSwitchingNode = false;
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
        return new ClashApiClient(
            Session.Settings.ClashApiPort,
            Session.Settings.ClashApiSecret);
    }

    private async Task RefreshWhenRunningAsync()
    {
        for (var attempt = 0; attempt < 20 && !Session.IsCoreRunning; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(50));

        if (Session.IsCoreRunning)
            await RefreshProxiesAsync();
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
        SelectedGroup = null;
        ProxyGroups.Clear();
        VisibleNodes.Clear();
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
