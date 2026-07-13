using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Tests;

public sealed class SubscriptionParserTests : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();
    private readonly SingBoxService core;
    private readonly SubscriptionParser parser;

    public SubscriptionParserTests()
    {
        var paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        parser = new SubscriptionParser(new SingBoxConfigService(paths, core));
    }

    [Fact]
    public void ParseClashYamlConvertsVlessGroupsAndRules()
    {
        const string yaml = """
                            mixed-port: 7890
                            proxies:
                              - name: Edge
                                type: vless
                                server: example.com
                                port: 443
                                uuid: 11111111-1111-1111-1111-111111111111
                                tls: true
                                network: ws
                                servername: example.com
                                client-fingerprint: chrome
                                ws-opts:
                                  path: /gateway
                                  headers:
                                    Host: example.com
                            proxy-groups:
                              - name: Select
                                type: select
                                proxies: [Edge, DIRECT]
                            rules:
                              - DOMAIN-SUFFIX,example.org,DIRECT
                              - MATCH,Select
                            """;
        var result = parser.Parse(yaml, CreateOptions());

        Assert.Equal(SubscriptionFormat.ClashYaml, result.Format);
        Assert.Equal(1, result.NodeCount);
        Assert.Equal(1, result.SourcePolicyGroupCount);
        Assert.Contains(
            result.Configuration["outbounds"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>(),
            outbound => outbound["type"]?.ToString() == "vless");
        var selector = result.Configuration["outbounds"]!.AsArray()
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Single(outbound => outbound["tag"]?.ToString() == "Select");
        Assert.DoesNotContain(
            result.Configuration["outbounds"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>(),
            outbound => outbound["type"]?.ToString() == "urltest");
        Assert.Equal("Edge", selector["default"]?.ToString());
        Assert.Equal("Edge", selector["outbounds"]?[0]?.ToString());
        Assert.Contains("domain_suffix", result.Configuration.ToJsonString());
        Assert.Equal("Select", result.Configuration["route"]?["final"]?.ToString());
    }

    [Fact]
    public void ParseBase64UriListBuildsManagedSelectors()
    {
        const string uri = "trojan://secret@example.com:443?security=tls&sni=example.com#Primary";
        var subscription = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri));
        var result = parser.Parse(subscription, CreateOptions());

        Assert.Equal(SubscriptionFormat.Base64UriList, result.Format);
        Assert.Equal(1, result.NodeCount);
        var outbounds = result.Configuration["outbounds"]!.AsArray()
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .ToArray();
        Assert.Contains(outbounds, outbound => outbound["tag"]?.ToString() == "Proxy");
        Assert.Contains(outbounds, outbound => outbound["type"]?.ToString() == "trojan");
    }

    [Fact]
    public void ParsePlainVlessUriListPreservesRealityOptions()
    {
        const string subscription =
            "vless://11111111-1111-1111-1111-111111111111@example.com:443"
            + "?type=tcp&encryption=none&flow=xtls-rprx-vision&security=reality"
            + "&sni=www.example.com&fp=chrome&pbk=public-key&sid=01234567"
            + "#IPv6%20%E6%97%A5%E6%9C%AC";
        var result = parser.Parse(subscription, CreateOptions());

        Assert.Equal(SubscriptionFormat.UriList, result.Format);
        Assert.Equal(1, result.NodeCount);
        Assert.Empty(result.Warnings);
        var outbound = result.Configuration["outbounds"]!.AsArray()
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Single(item => item["type"]?.ToString() == "vless");
        Assert.Equal("IPv6 日本", outbound["tag"]?.ToString());
        Assert.Equal("xtls-rprx-vision", outbound["flow"]?.ToString());
        Assert.Equal("www.example.com", outbound["tls"]?["server_name"]?.ToString());
        Assert.Equal("chrome", outbound["tls"]?["utls"]?["fingerprint"]?.ToString());
        Assert.Equal("public-key", outbound["tls"]?["reality"]?["public_key"]?.ToString());
        Assert.Equal("01234567", outbound["tls"]?["reality"]?["short_id"]?.ToString());
    }

    [Fact]
    public void ParseClashYamlConvertsGeoRulesToModernRuleSets()
    {
        const string yaml = """
                            proxies:
                              - name: Edge
                                type: vless
                                server: example.com
                                port: 443
                                uuid: 11111111-1111-1111-1111-111111111111
                            proxy-groups:
                              - name: Source group
                                type: select
                                proxies: [Edge, DIRECT]
                            rules:
                              - GEOIP,CN,DIRECT
                              - GEOIP,LAN,DIRECT,no-resolve
                              - GEOSITE,CN,Source group
                              - MATCH,Source group
                            """;
        var result = parser.Parse(yaml, CreateOptions());

        Assert.Empty(result.Warnings);
        var route = result.Configuration["route"]!.AsObject();
        var ruleSets = route["rule_set"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        Assert.Collection(
            ruleSets,
            geoIp =>
            {
                Assert.Equal("geoip-cn", geoIp["tag"]?.ToString());
                Assert.Equal("binary", geoIp["format"]?.ToString());
                Assert.Equal("direct", geoIp["download_detour"]?.ToString());
                Assert.EndsWith("/geoip-cn.srs", geoIp["url"]?.ToString());
            },
            geoSite =>
            {
                Assert.Equal("geosite-cn", geoSite["tag"]?.ToString());
                Assert.EndsWith("/geosite-cn.srs", geoSite["url"]?.ToString());
            });
        var rules = route["rules"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        Assert.Contains(rules, rule => rule["rule_set"]?[0]?.ToString() == "geoip-cn");
        Assert.Contains(rules, rule => rule["rule_set"]?[0]?.ToString() == "geosite-cn");
        Assert.Contains(rules, rule => rule["ip_is_private"]?.GetValue<bool>() == true);
    }

    [Fact]
    public void ParseBase64UriListRejectsInvalidUtf8()
    {
        var prefix = Encoding.UTF8.GetBytes("trojan://secret@example.com:443#");
        var bytes = new byte[prefix.Length + 1];
        prefix.CopyTo(bytes, 0);
        bytes[^1] = 0xff;
        var subscription = Convert.ToBase64String(bytes);
        Assert.Throws<InvalidDataException>(() => parser.Parse(subscription, CreateOptions()));
    }

    [Fact]
    public void ParseSingBoxJsonPreservesUnknownTopLevelFeatures()
    {
        const string json = """
                            {
                              "services": [{ "type": "resolved", "tag": "resolver" }],
                              "outbounds": [{ "type": "direct", "tag": "direct" }],
                              "route": { "final": "direct" }
                            }
                            """;
        var result = parser.Parse(json, CreateOptions());

        Assert.Equal(SubscriptionFormat.SingBoxJson, result.Format);
        Assert.NotNull(result.Configuration["services"]);
        Assert.NotNull(result.Configuration["experimental"]?["clash_api"]);
    }

    [Fact]
    public void ParseSingBoxJsonPreservesNativeGroupsAndRuleSets()
    {
        const string json = """
                            {
                              "outbounds": [
                                {
                                  "type": "vless",
                                  "tag": "Edge",
                                  "server": "example.com",
                                  "server_port": 443,
                                  "uuid": "11111111-1111-1111-1111-111111111111"
                                },
                                { "type": "urltest", "tag": "Auto", "outbounds": ["Edge"] },
                                { "type": "selector", "tag": "Proxy", "outbounds": ["Auto", "Edge"] },
                                { "type": "direct", "tag": "direct" }
                              ],
                              "route": {
                                "rules": [
                                  { "rule_set": ["geoip-cn"], "action": "route", "outbound": "direct" }
                                ],
                                "rule_set": [
                                  {
                                    "type": "remote",
                                    "tag": "geoip-cn",
                                    "format": "binary",
                                    "url": "https://example.invalid/geoip-cn.srs",
                                    "download_detour": "direct"
                                  }
                                ],
                                "final": "Proxy"
                              }
                            }
                            """;
        var result = parser.Parse(json, CreateOptions());

        Assert.Equal(SubscriptionFormat.SingBoxJson, result.Format);
        var outbounds = result.Configuration["outbounds"]!.AsArray();
        Assert.Contains(outbounds, outbound => outbound?["tag"]?.ToString() == "Auto");
        Assert.Contains(outbounds, outbound => outbound?["tag"]?.ToString() == "Proxy");
        Assert.Equal("geoip-cn", result.Configuration["route"]?["rule_set"]?[0]?["tag"]?.ToString());
        Assert.Equal("Proxy", result.Configuration["route"]?["final"]?.ToString());
    }

    [Fact]
    public void ParseSingBoxJsonRemovesAndroidOnlyRouteOptionOnDesktop()
    {
        const string json = """
                            {
                              "outbounds": [{ "type": "direct", "tag": "direct" }],
                              "route": { "final": "direct", "override_android_vpn": true }
                            }
                            """;
        var result = parser.Parse(json, CreateOptions());

        Assert.Null(result.Configuration["route"]?["override_android_vpn"]);
    }

    [Fact]
    public void ParseSingBoxJsonRemovesTunWhenOptionIsDisabled()
    {
        const string json = """
                            {
                              "inbounds": [
                                { "type": "mixed", "tag": "mixed-in" },
                                { "type": "tun", "tag": "provider-tun", "auto_route": true }
                              ],
                              "outbounds": [{ "type": "direct", "tag": "direct" }]
                            }
                            """;
        var result = parser.Parse(json, CreateOptions());

        Assert.DoesNotContain(
            result.Configuration["inbounds"]!.AsArray(),
            inbound => inbound?["type"]?.ToString() == "tun");
    }

    private static SubscriptionBuildOptions CreateOptions()
    {
        return new SubscriptionBuildOptions
        {
            MixedPort = 20_80,
            ClashApiPort = 19_090,
            ClashApiSecret = "test-secret",
            EnableSystemProxy = false,
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await core.DisposeAsync();
        directory.Dispose();
    }
}
