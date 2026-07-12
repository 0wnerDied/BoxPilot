using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Tests;

public sealed class SubscriptionParserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boxpilot-tests-{Guid.NewGuid():N}");

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
        var parser = CreateParser();

        var result = parser.Parse(yaml, CreateOptions());

        Assert.Equal(SubscriptionFormat.ClashYaml, result.Format);
        Assert.Equal(1, result.NodeCount);
        Assert.Contains(
            result.Configuration["outbounds"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>(),
            outbound => outbound["type"]?.ToString() == "vless");
        Assert.Contains("domain_suffix", result.Configuration.ToJsonString());
        Assert.Equal("Select", result.Configuration["route"]?["final"]?.ToString());
    }

    [Fact]
    public void ParseBase64UriListBuildsManagedSelectors()
    {
        const string uri = "trojan://secret@example.com:443?security=tls&sni=example.com#Primary";
        var subscription = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri));
        var parser = CreateParser();

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
    public void ParseSingBoxJsonPreservesUnknownTopLevelFeatures()
    {
        const string json = """
                            {
                              "services": [{ "type": "resolved", "tag": "resolver" }],
                              "outbounds": [{ "type": "direct", "tag": "direct" }],
                              "route": { "final": "direct" }
                            }
                            """;
        var parser = CreateParser();

        var result = parser.Parse(json, CreateOptions());

        Assert.Equal(SubscriptionFormat.SingBoxJson, result.Format);
        Assert.NotNull(result.Configuration["services"]);
        Assert.NotNull(result.Configuration["experimental"]?["clash_api"]);
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
        var parser = CreateParser();

        var result = parser.Parse(json, CreateOptions());

        Assert.Null(result.Configuration["route"]?["override_android_vpn"]);
    }

    private SubscriptionParser CreateParser()
    {
        var paths = new AppPaths(root);
        var core = new SingBoxService(paths);
        var config = new SingBoxConfigService(paths, core);
        return new SubscriptionParser(config);
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

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
