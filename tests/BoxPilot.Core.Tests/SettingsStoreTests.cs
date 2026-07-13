using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    [Fact]
    public async Task FirstLoadCreatesPersistentApiSecret()
    {
        var store = new SettingsStore(new AppPaths(directory.Path));

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.Equal(48, first.ClashApiSecret.Length);
        Assert.Equal(first.ClashApiSecret, second.ClashApiSecret);
        Assert.Equal("zh-CN", first.Language);
        Assert.Equal("Light", first.Theme);
        Assert.Equal("Rule", first.RoutingMode);
    }

    public void Dispose()
    {
        directory.Dispose();
    }
}
