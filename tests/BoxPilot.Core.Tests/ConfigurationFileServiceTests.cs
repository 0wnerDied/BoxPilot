using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class ConfigurationFileServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();
    private readonly AppPaths paths;
    private readonly SingBoxService core;
    private readonly ProfileRepository repository;
    private readonly ConfigurationFileService files;

    public ConfigurationFileServiceTests()
    {
        paths = new AppPaths(Path.Combine(directory.Path, "data"));
        core = new SingBoxService(paths);
        var config = new SingBoxConfigService(paths, core);
        repository = new ProfileRepository(paths);
        files = new ConfigurationFileService(config, repository);
    }

    [Fact]
    public async Task ImportPreservesNativeJsonAndRelativeAssetDirectory()
    {
        try
        {
            await core.InitializeAsync();
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var sourceDirectory = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "private.json"),
            """{ "version": 3, "rules": [] }""");
        var configuration = """
            {
              "inbounds": [
                { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": 2080 }
              ],
              "outbounds": [
                { "type": "socks", "tag": "Edge", "server": "127.0.0.1", "server_port": 1080 },
                { "type": "direct", "tag": "direct" }
              ],
              "route": {
                "rules": [
                  { "rule_set": "private", "action": "route", "outbound": "direct" }
                ],
                "rule_set": [
                  { "type": "local", "tag": "private", "format": "source", "path": "private.json" }
                ],
                "final": "Edge"
              }
            }
            """;
        var sourcePath = Path.Combine(sourceDirectory, "client.json");
        await File.WriteAllTextAsync(sourcePath, configuration, new UTF8Encoding(false, true));

        var profile = await files.ImportAsync(null, sourcePath);

        Assert.Equal(ProfileSource.ImportedFile, profile.Source);
        Assert.Equal("client", profile.Name);
        Assert.Equal(sourceDirectory, profile.WorkingDirectory);
        Assert.Equal(1, profile.NodeCount);
        Assert.Equal(configuration, await repository.ReadConfigurationAsync(profile));
    }

    [Fact]
    public async Task ExportWritesStrictUtf8WithoutBom()
    {
        var destination = Path.Combine(directory.Path, "export", "配置.json");

        await files.ExportAsync("{\"tag\":\"日本节点\"}", destination);

        var bytes = await File.ReadAllBytesAsync(destination);
        Assert.False(bytes is [0xef, 0xbb, 0xbf, ..]);
        Assert.Equal("{\"tag\":\"日本节点\"}", new UTF8Encoding(false, true).GetString(bytes));
    }

    [Fact]
    public async Task ImportMergesNativeFragmentsFromOneAssetDirectory()
    {
        try
        {
            await core.InitializeAsync();
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var sourceDirectory = Path.Combine(directory.Path, "fragments");
        Directory.CreateDirectory(sourceDirectory);
        var basePath = Path.Combine(sourceDirectory, "00-base.json");
        var routePath = Path.Combine(sourceDirectory, "10-route.json");
        await File.WriteAllTextAsync(basePath, """
            {
              "inbounds": [
                { "type": "mixed", "listen": "127.0.0.1", "listen_port": 2080 }
              ],
              "outbounds": [
                { "type": "direct", "tag": "direct" }
              ]
            }
            """);
        await File.WriteAllTextAsync(routePath, """
            {
              "log": { "level": "warn", "timestamp": true },
              "route": { "final": "direct" }
            }
            """);

        var profile = await files.ImportAsync("Merged", [basePath, routePath]);
        var configuration = await repository.ReadConfigurationAsync(profile);

        Assert.Equal("Merged", profile.Name);
        Assert.Equal(sourceDirectory, profile.WorkingDirectory);
        Assert.Contains("\"level\": \"warn\"", configuration);
        Assert.Contains("\"type\": \"mixed\"", configuration);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await core.DisposeAsync();
        directory.Dispose();
    }
}
