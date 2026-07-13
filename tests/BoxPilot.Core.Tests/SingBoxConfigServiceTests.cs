using System.Text.Json.Nodes;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SingBoxConfigServiceTests : SingBoxTestBase
{
    [Fact]
    public void SerializeAndFormatPreserveUnicodeText()
    {
        var configuration = new JsonObject
        {
            ["tag"] = "IPv6 日本 A01 移动宽带优化",
        };

        var json = Config.Serialize(configuration);
        var formatted = Config.FormatJson("""
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

        var prepared = Config.PrepareManagedSubscription(configuration, "subscription-test");
        var preparedAgain = Config.PrepareManagedSubscription(prepared, "subscription-test");

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

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings
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
    public void ApplyRuntimeOptionsEnablesAddressReuseForMixedInbound()
    {
        var sourceMixed = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["reuse_addr"] = false,
        };
        var configuration = new JsonObject
        {
            ["inbounds"] = new JsonArray(sourceMixed),
        };

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings());

        var mixed = Assert.Single(result["inbounds"]!.AsArray().OfType<JsonObject>());
        Assert.True(mixed["reuse_addr"]!.GetValue<bool>());
        Assert.False(sourceMixed["reuse_addr"]!.GetValue<bool>());
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

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings
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

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings());
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

        var prepared = Config.PrepareManagedSubscription(
            configuration,
            "subscription-test",
            preservePolicyGroups: true);

        Assert.DoesNotContain(
            prepared["outbounds"]!.AsArray().OfType<JsonObject>(),
            outbound => outbound["type"]?.ToString() == "urltest");
        Assert.Equal("Edge", prepared["outbounds"]?[1]?["default"]?.ToString());
        Assert.Equal("subscription-test", prepared["experimental"]?["cache_file"]?["cache_id"]?.ToString());
    }

    [Fact]
    public async Task AddStandardRoutingModesPreservesRulesAndAddsMissingDirectOutbound()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "Proxy",
                    ["outbounds"] = new JsonArray("Edge"),
                },
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "Edge",
                    ["server"] = "example.test",
                    ["server_port"] = 1080,
                }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["domain_suffix"] = new JsonArray("example.test"), ["outbound"] = "direct" }),
                ["final"] = "Proxy",
            },
        };

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings
        {
            AllowLan = true,
            CustomDnsServer = "https://1.1.1.1/dns-query",
            RoutingMode = "Global",
        });
        Assert.False(Config.SupportsStandardRoutingModes(result));
        Assert.True(Config.CanAddStandardRoutingModes(Config.Serialize(result)));

        result = Config.AddStandardRoutingModes(result);

        var mixed = result["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]?.ToString() == "mixed");
        Assert.Equal("0.0.0.0", mixed["listen"]?.ToString());
        Assert.Equal("global", result["experimental"]?["clash_api"]?["default_mode"]?.ToString());
        var rules = result["route"]!["rules"]!.AsArray();
        Assert.Equal("sniff", rules[0]?["action"]?.ToString());
        Assert.Equal("direct", rules[1]?["clash_mode"]?.ToString());
        Assert.Equal("boxpilot-direct", rules[1]?["outbound"]?.ToString());
        Assert.Equal("global", rules[2]?["clash_mode"]?.ToString());
        Assert.Equal(SingBoxConfigService.ManagedGlobalSelectorTag, rules[2]?["outbound"]?.ToString());
        Assert.Contains(
            result["outbounds"]!.AsArray().OfType<JsonObject>(),
            outbound => outbound["type"]?.ToString() == "direct"
                        && outbound["tag"]?.ToString() == "boxpilot-direct");
        var global = result["outbounds"]!.AsArray().OfType<JsonObject>()
            .Single(outbound => outbound["tag"]?.ToString() == SingBoxConfigService.ManagedGlobalSelectorTag);
        Assert.Equal("selector", global["type"]?.ToString());
        Assert.Equal(["Edge"], global["outbounds"]!.AsArray().Select(static item => item!.ToString()));
        Assert.Equal("Edge", global["default"]?.ToString());
        Assert.DoesNotContain(
            configuration["outbounds"]!.AsArray().OfType<JsonObject>(),
            outbound => outbound["type"]?.ToString() == "direct");
        Assert.Equal("https", result["dns"]?["servers"]?[0]?["type"]?.ToString());
        Assert.Equal("/dns-query", result["dns"]?["servers"]?[0]?["path"]?.ToString());
        var validation = await Config.ValidateAsync(Config.Serialize(result));
        Assert.True(validation.IsSuccess, validation.CombinedOutput);
    }

    [Fact]
    public void EnsureManagedStandardRoutingModesKeepsGlobalSelectionIndependent()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = "Provider",
                    ["outbounds"] = new JsonArray("Automatic", "Edge A", "Edge B"),
                    ["default"] = "Edge A",
                },
                new JsonObject
                {
                    ["type"] = "urltest",
                    ["tag"] = "Automatic",
                    ["outbounds"] = new JsonArray("Edge A", "Edge B"),
                },
                new JsonObject { ["type"] = "socks", ["tag"] = "Edge A" },
                new JsonObject { ["type"] = "socks", ["tag"] = "Edge B" },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["clash_mode"] = "global",
                        ["action"] = "route",
                        ["outbound"] = "Provider",
                    },
                    new JsonObject
                    {
                        ["clash_mode"] = "direct",
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    }),
                ["final"] = "Provider",
            },
        };

        var result = Config.EnsureManagedStandardRoutingModes(configuration);
        result = Config.EnsureManagedStandardRoutingModes(result);

        var outbounds = result["outbounds"]!.AsArray().OfType<JsonObject>().ToArray();
        var managed = Assert.Single(outbounds, outbound =>
            SingBoxConfigService.IsManagedGlobalSelector(outbound["tag"]?.ToString()));
        Assert.Equal(
            ["Automatic", "Edge A", "Edge B"],
            managed["outbounds"]!.AsArray().Select(static item => item!.ToString()));
        Assert.Equal("Edge A", managed["default"]?.ToString());
        Assert.Equal(
            SingBoxConfigService.ManagedGlobalSelectorTag,
            result["route"]!["rules"]![0]!["outbound"]?.ToString());
        Assert.Equal("Provider", result["route"]!["final"]?.ToString());
        Assert.Equal(SingBoxConfigService.ManagedGlobalSelectorTag, Config.GetGlobalProxyGroup(result));
        Assert.Equal(
            ["Automatic", "Edge A", "Edge B"],
            outbounds.Single(outbound => outbound["tag"]?.ToString() == "Provider")["outbounds"]!
                .AsArray()
                .Select(static item => item!.ToString()));
    }

    [Fact]
    public void ExplicitRoutingModesPreserveProviderRules()
    {
        var configuration = new JsonObject
        {
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "selector", ["tag"] = "Provider global" },
                new JsonObject { ["type"] = "selector", ["tag"] = "Provider default" },
                new JsonObject { ["type"] = "direct", ["tag"] = "provider-direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["clash_mode"] = "global",
                        ["action"] = "route",
                        ["outbound"] = "Provider global",
                    },
                    new JsonObject
                    {
                        ["clash_mode"] = "direct",
                        ["action"] = "route",
                        ["outbound"] = "provider-direct",
                    }),
                ["final"] = "Provider default",
            },
        };

        var result = Config.ApplyRuntimeOptions(configuration, new AppSettings());
        result = Config.ApplyRuntimeOptions(result, new AppSettings());
        result = Config.AddStandardRoutingModes(result);
        var rules = result["route"]!["rules"]!.AsArray();

        Assert.Equal(2, rules.Count);
        Assert.Equal("Provider global", rules[0]?["outbound"]?.ToString());
        Assert.Equal("provider-direct", rules[1]?["outbound"]?.ToString());
    }

    [Fact]
    public void ReadsNativeClashApiAndStandardModeCapabilities()
    {
        const string configuration = """
            {
              "route": {
                "rules": [
                  { "clash_mode": "direct", "action": "route", "outbound": "direct" },
                  { "clash_mode": "global", "action": "route", "outbound": "Proxy" }
                ]
              },
              "experimental": {
                "clash_api": {
                  "external_controller": "0.0.0.0:19090",
                  "secret": "native-secret"
                }
              }
            }
            """;

        var connection = Config.GetClashApiConnection(configuration);

        Assert.Equal(19090, connection?.Port);
        Assert.Equal("native-secret", connection?.Secret);
        Assert.True(Config.SupportsStandardRoutingModes(configuration));
    }
}
