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
}
