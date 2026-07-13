using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    [Fact]
    public async Task CreateUpdateAndDeleteRoundTripsProfile()
    {
        var repository = new ProfileRepository(new AppPaths(directory.Path));
        var profile = await repository.CreateAsync("Test", "{}", ProfileSource.Manual);

        Assert.Equal("{}", await repository.ReadConfigurationAsync(profile));
        Assert.Single(await repository.GetAllAsync());

        var updated = profile with { Name = "Updated", NodeCount = 3 };
        await repository.UpdateAsync(updated);
        var loaded = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Updated", loaded.Name);
        Assert.Equal(3, loaded.NodeCount);

        await repository.DeleteAsync(profile.Id);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task ConfigurationRoundTripsUtf8WithoutBom()
    {
        var paths = new AppPaths(directory.Path);
        var repository = new ProfileRepository(paths);
        const string configuration = """
            {
              "remarks": "中文节点 🚀"
            }
            """;

        var profile = await repository.CreateAsync("中文配置", configuration);

        Assert.Equal(configuration, await repository.ReadConfigurationAsync(profile));
        var bytes = await File.ReadAllBytesAsync(paths.GetProfileConfigPath(profile));
        Assert.False(HasUtf8Bom(bytes));
        Assert.Equal(configuration, new UTF8Encoding(false, true).GetString(bytes));
        var index = await File.ReadAllTextAsync(
            paths.ProfileIndexFile,
            new UTF8Encoding(false, true));
        Assert.Contains("中文配置", index);
        Assert.False(index.Contains("\\u4e2d", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadConfigurationRejectsInvalidUtf8()
    {
        var paths = new AppPaths(directory.Path);
        var repository = new ProfileRepository(paths);
        var profile = await repository.CreateAsync("Test", "{}");
        await File.WriteAllBytesAsync(paths.GetProfileConfigPath(profile), [0x7b, 0xff, 0x7d]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => repository.ReadConfigurationAsync(profile));
    }

    [Fact]
    public async Task ConcurrentCreatesPreserveEveryProfile()
    {
        var repository = new ProfileRepository(new AppPaths(directory.Path));

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => repository.CreateAsync($"Profile {index}", "{}")));

        var profiles = await repository.GetAllAsync();
        Assert.Equal(16, profiles.Count);
        Assert.Equal(16, profiles.Select(profile => profile.Id).Distinct().Count());
    }

    [Fact]
    public async Task CanceledReadDoesNotEnterRepositoryGate()
    {
        var repository = new ProfileRepository(new AppPaths(directory.Path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetAllAsync(cancellation.Token));
    }

    public void Dispose()
    {
        directory.Dispose();
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes is [0xef, 0xbb, 0xbf, ..];
    }
}
