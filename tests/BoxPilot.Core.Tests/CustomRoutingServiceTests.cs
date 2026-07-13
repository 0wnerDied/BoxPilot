using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class CustomRoutingServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();
    private readonly AppPaths paths;
    private readonly SingBoxService core;
    private readonly SingBoxConfigService config;
    private readonly CustomRoutingService routing;

    public CustomRoutingServiceTests()
    {
        paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        config = new SingBoxConfigService(paths, core);
        routing = new CustomRoutingService(paths, config);
    }

    [Fact]
    public async Task ApplyPreservesProviderRulesAndOrdersManagedRulesBeforeThem()
    {
        var profileId = Guid.NewGuid();
        var localId = Guid.NewGuid();
        var localFile = $"{localId:N}.json";
        Directory.CreateDirectory(paths.GetRuleSetDirectory(profileId));
        await File.WriteAllTextAsync(
            Path.Combine(paths.GetRuleSetDirectory(profileId), localFile),
            """{ "version": 3, "rules": [] }""");
        var configuration = config.AddStandardRoutingModes(
            config.ApplyRuntimeOptions(CreateConfiguration(), new AppSettings()));
        var ruleSets = new CustomRuleSet[]
        {
            new()
            {
                Id = localId,
                ProfileId = profileId,
                Name = "Local",
                FileName = localFile,
                Outbound = "direct",
                Format = RuleSetFormat.Source,
                Source = RuleSetSource.Local,
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Name = "Remote",
                Url = "https://rules.example.test/proxy.srs",
                Outbound = "Proxy",
                Format = RuleSetFormat.Binary,
                Source = RuleSetSource.Remote,
            },
        };

        var result = routing.Apply(configuration, ruleSets);

        var rules = result["route"]!["rules"]!.AsArray();
        Assert.Equal("sniff", rules[0]?["action"]?.ToString());
        Assert.Equal("direct", rules[2]?["clash_mode"]?.ToString());
        Assert.Equal(ruleSets[0].Tag, rules[4]?["rule_set"]?[0]?.ToString());
        Assert.Equal(ruleSets[1].Tag, rules[5]?["rule_set"]?[0]?.ToString());
        Assert.Equal("provider.test", rules[6]?["domain_suffix"]?[0]?.ToString());

        var definitions = result["route"]!["rule_set"]!.AsArray();
        Assert.Equal("local", definitions[0]?["type"]?.ToString());
        Assert.Equal("source", definitions[0]?["format"]?.ToString());
        Assert.Equal("remote", definitions[1]?["type"]?.ToString());
        Assert.Equal("https://rules.example.test/proxy.srs", definitions[1]?["url"]?.ToString());
        Assert.Equal("direct", definitions[1]?["download_detour"]?.ToString());
    }

    [Fact]
    public void ApplyWithoutManagedRulesPreservesCompleteNativeConfiguration()
    {
        var configuration = new JsonObject
        {
            ["ntp"] = new JsonObject { ["enabled"] = true, ["server"] = "time.example.test" },
            ["certificate"] = new JsonObject
            {
                ["certificate_directory_path"] = new JsonArray("certificates"),
            },
            ["endpoints"] = new JsonArray(new JsonObject
            {
                ["type"] = "tailscale",
                ["tag"] = "tailnet",
            }),
            ["services"] = new JsonArray(new JsonObject
            {
                ["type"] = "derp",
                ["tag"] = "relay",
            }),
        };

        var result = routing.Apply(configuration, []);

        Assert.True(JsonNode.DeepEquals(configuration, result));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await core.DisposeAsync();
        directory.Dispose();
    }

    private static JsonObject CreateConfiguration()
    {
        return new JsonObject
        {
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
            }),
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "Proxy",
                    ["outbounds"] = new JsonArray("Edge"),
                },
                new JsonObject { ["type"] = "socks", ["tag"] = "Edge" },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                    new JsonObject
                    {
                        ["domain_suffix"] = new JsonArray("provider.test"),
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    }),
                ["final"] = "Proxy",
            },
        };
    }
}
