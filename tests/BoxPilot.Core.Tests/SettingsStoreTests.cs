using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boxpilot-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task FirstLoadCreatesPersistentApiSecret()
    {
        var store = new SettingsStore(new AppPaths(root));

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.Equal(48, first.ClashApiSecret.Length);
        Assert.Equal(first.ClashApiSecret, second.ClashApiSecret);
        Assert.Equal("zh-CN", first.Language);
        Assert.Equal("Light", first.Theme);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
