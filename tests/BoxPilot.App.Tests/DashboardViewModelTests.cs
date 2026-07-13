using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task SelectingGroupPairsNodesIntoRowsInProviderOrder()
    {
        using var directory = new TemporaryDirectory();
        await using var runtime = new AppRuntime(new AppPaths(directory.Path));
        var nodes = Enumerable.Range(1, 5)
            .Select(index => new ProxyNode($"node-{index}", "VLESS", null, true, false))
            .ToArray();
        var group = new ProxyGroupItemViewModel(
            new ProxyChoice("manual", true, nodes[0].Name, nodes),
            runtime.Localization,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask);
        var viewModel = new DashboardViewModel(runtime.Session, runtime.Localization)
        {
            SelectedGroup = group,
        };

        Assert.Collection(
            viewModel.VisibleNodeRows,
            row => AssertRow(row, group.Nodes[0], group.Nodes[1]),
            row => AssertRow(row, group.Nodes[2], group.Nodes[3]),
            row => AssertRow(row, group.Nodes[4], null));
        Assert.Equal("5", viewModel.NodeCountDisplay);

        viewModel.SearchText = "node-4";

        var filtered = Assert.Single(viewModel.VisibleNodeRows);
        AssertRow(filtered, group.Nodes[3], null);
        Assert.Equal("1/5", viewModel.NodeCountDisplay);
    }

    private static void AssertRow(
        ProxyNodeRowViewModel row,
        ProxyNodeItemViewModel first,
        ProxyNodeItemViewModel? second)
    {
        Assert.Same(first, row.First);
        Assert.Same(second, row.Second);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"boxpilot-app-tests-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
