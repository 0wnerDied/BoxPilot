using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SingBoxConfigServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();
    private readonly SingBoxService core;
    private readonly SingBoxConfigService service;

    public SingBoxConfigServiceTests()
    {
        var paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        service = new SingBoxConfigService(paths, core);
    }

    [Fact]
    public void SerializeAndFormatPreserveUnicodeText()
    {
        var configuration = new JsonObject
        {
            ["tag"] = "IPv6 日本 A01 移动宽带优化",
        };

        var json = service.Serialize(configuration);
        var formatted = service.FormatJson("""
            { "tag": "IPv6 \u65E5\u672C A01 \u79FB\u52A8\u5BBD\u5E26\u4F18\u5316" }
            """);

        Assert.Contains("IPv6 日本 A01 移动宽带优化", json);
        Assert.False(json.Contains("\\u65e5", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("IPv6 日本 A01 移动宽带优化", formatted);
        Assert.False(formatted.Contains("\\u65e5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrepareManagedSubscriptionDefaultsToAutomaticSelection()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "vless", ["tag"] = "Unavailable" },
                new JsonObject { ["type"] = "vless", ["tag"] = "Available" },
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "Proxy",
                    ["outbounds"] = new JsonArray("Unavailable", "Available"),
                    ["default"] = "Unavailable",
                }),
        };

        var prepared = service.PrepareManagedSubscription(configuration, "subscription-test");
        var preparedAgain = service.PrepareManagedSubscription(prepared, "subscription-test");

        var outbounds = prepared["outbounds"]!.AsArray();
        var automatic = outbounds.OfType<JsonObject>()
            .Single(outbound => outbound["type"]?.ToString() == "urltest");
        var selector = outbounds.OfType<JsonObject>()
            .Single(outbound => outbound["type"]?.ToString() == "selector");
        Assert.Equal(automatic["tag"]?.ToString(), selector["default"]?.ToString());
        Assert.Equal(automatic["tag"]?.ToString(), selector["outbounds"]?[0]?.ToString());
        Assert.Equal("subscription-test", prepared["experimental"]?["cache_file"]?["cache_id"]?.ToString());
        Assert.Equal("Unavailable", configuration["outbounds"]?[2]?["default"]?.ToString());
        Assert.Single(
            preparedAgain["outbounds"]!.AsArray().OfType<JsonObject>(),
            outbound => outbound["type"]?.ToString() == "urltest");
    }

    [Fact]
    public void ApplyRuntimeOptionsRemovesImportedTunWhenDisabled()
    {
        var configuration = new JsonObject
        {
            ["inbounds"] = new JsonArray(
                new JsonObject { ["type"] = "tun", ["tag"] = "in" },
                new JsonObject { ["type"] = "TUN", ["tag"] = "secondary" },
                new JsonObject { ["type"] = "mixed", ["tag"] = "mixed-in" }),
        };

        var result = service.ApplyRuntimeOptions(configuration, new AppSettings
        {
            EnableTun = false,
        });

        Assert.DoesNotContain(
            result["inbounds"]!.AsArray().OfType<JsonObject>(),
            inbound => string.Equals(
                inbound["type"]?.ToString(),
                "tun",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, configuration["inbounds"]!.AsArray().Count);
    }

    [Fact]
    public void ApplyRuntimeOptionsKeepsImportedTunWhenEnabled()
    {
        var importedTun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "provider-tun",
            ["address"] = new JsonArray("10.0.0.1/30"),
        };
        var configuration = new JsonObject
        {
            ["inbounds"] = new JsonArray(importedTun),
        };

        var result = service.ApplyRuntimeOptions(configuration, new AppSettings
        {
            EnableTun = true,
        });

        var tun = Assert.Single(
            result["inbounds"]!.AsArray().OfType<JsonObject>(),
            inbound => inbound["type"]?.ToString() == "tun");
        Assert.Equal("provider-tun", tun["tag"]?.ToString());
        Assert.Equal("10.0.0.1/30", tun["address"]?[0]?.ToString());
    }

    [Fact]
    public void ApplyRuntimeOptionsBootstrapsRemoteRulesThroughDirectOutbound()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "rules_download",
                    ["outbounds"] = new JsonArray("auto", "direct"),
                },
                new JsonObject { ["type"] = "urltest", ["tag"] = "auto" },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "vless", ["tag"] = "fixed-proxy" }),
            ["route"] = new JsonObject
            {
                ["rule_set"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "remote",
                        ["tag"] = "group-download",
                        ["url"] = "https://example.invalid/group.srs",
                        ["download_detour"] = "rules_download",
                    },
                    new JsonObject
                    {
                        ["type"] = "remote",
                        ["tag"] = "fixed-download",
                        ["url"] = "https://example.invalid/fixed.srs",
                        ["download_detour"] = "fixed-proxy",
                    }),
            },
        };

        var result = service.ApplyRuntimeOptions(configuration, new AppSettings());
        var ruleSets = result["route"]!["rule_set"]!.AsArray();

        Assert.Equal("direct", ruleSets[0]!["download_detour"]?.ToString());
        Assert.Equal("fixed-proxy", ruleSets[1]!["download_detour"]?.ToString());
    }

    [Fact]
    public void PrepareManagedSubscriptionCanPreserveSourcePolicyGroups()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "vless", ["tag"] = "Edge" },
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "Source group",
                    ["outbounds"] = new JsonArray("Edge", "direct"),
                    ["default"] = "Edge",
                }),
        };

        var prepared = service.PrepareManagedSubscription(
            configuration,
            "subscription-test",
            preservePolicyGroups: true);

        Assert.DoesNotContain(
            prepared["outbounds"]!.AsArray().OfType<JsonObject>(),
            outbound => outbound["type"]?.ToString() == "urltest");
        Assert.Equal("Edge", prepared["outbounds"]?[1]?["default"]?.ToString());
        Assert.Equal("subscription-test", prepared["experimental"]?["cache_file"]?["cache_id"]?.ToString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await core.DisposeAsync();
        directory.Dispose();
    }
}
