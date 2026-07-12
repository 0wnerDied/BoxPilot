using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boxpilot-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateUpdateAndDeleteRoundTripsProfile()
    {
        var repository = new ProfileRepository(new AppPaths(root));
        var profile = await repository.CreateAsync("Test", "{}", ProfileSource.Manual);

        Assert.Equal("{}", await repository.ReadConfigurationAsync(profile));
        Assert.Single(await repository.GetAllAsync());

        var updated = profile with { Name = "Updated", NodeCount = 3 };
        await repository.UpdateAsync(updated);
        var loaded = await repository.FindAsync(profile.Id);
        Assert.Equal("Updated", loaded?.Name);
        Assert.Equal(3, loaded?.NodeCount);

        await repository.DeleteAsync(profile.Id);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task ConfigurationRoundTripsUtf8WithoutBom()
    {
        var paths = new AppPaths(root);
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
        var paths = new AppPaths(root);
        var repository = new ProfileRepository(paths);
        var profile = await repository.CreateAsync("Test", "{}");
        await File.WriteAllBytesAsync(paths.GetProfileConfigPath(profile), [0x7b, 0xff, 0x7d]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => repository.ReadConfigurationAsync(profile));
    }

    [Fact]
    public async Task ConcurrentCreatesPreserveEveryProfile()
    {
        var repository = new ProfileRepository(new AppPaths(root));

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => repository.CreateAsync($"Profile {index}", "{}")));

        var profiles = await repository.GetAllAsync();
        Assert.Equal(16, profiles.Count);
        Assert.Equal(16, profiles.Select(profile => profile.Id).Distinct().Count());
    }

    [Fact]
    public async Task CanceledReadDoesNotEnterRepositoryGate()
    {
        var repository = new ProfileRepository(new AppPaths(root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetAllAsync(cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes is [0xef, 0xbb, 0xbf, ..];
    }

}
