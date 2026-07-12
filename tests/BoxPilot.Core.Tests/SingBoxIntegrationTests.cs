using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SingBoxIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boxpilot-tests-{Guid.NewGuid():N}");
    private SingBoxService? core;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledCoreAcceptsStarterConfiguration(bool enableTun)
    {
        var paths = new AppPaths(root);
        core = new SingBoxService(paths);
        try
        {
            await core.InitializeAsync();
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var configService = new SingBoxConfigService(paths, core);
        var settings = new AppSettings
        {
            EnableTun = enableTun,
            EnableSystemProxy = false,
            ClashApiPort = 19_091,
            ClashApiSecret = "integration-test",
        };
        var configuration = configService.Serialize(configService.CreateStarterConfiguration(settings));

        var result = await configService.ValidateAsync(configuration);

        Assert.True(result.IsSuccess, result.CombinedOutput);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (core is not null)
            await core.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
