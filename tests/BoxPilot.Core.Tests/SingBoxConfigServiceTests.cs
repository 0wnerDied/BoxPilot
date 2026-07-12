using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SingBoxConfigServiceTests
{
    [Fact]
    public async Task SerializeAndFormatPreserveUnicodeText()
    {
        var paths = new AppPaths(Path.Combine(
            Path.GetTempPath(),
            $"boxpilot-tests-{Guid.NewGuid():N}"));
        await using var core = new SingBoxService(paths);
        var service = new SingBoxConfigService(paths, core);
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
    public async Task PrepareManagedSubscriptionDefaultsToAutomaticSelection()
    {
        var paths = new AppPaths(Path.Combine(
            Path.GetTempPath(),
            $"boxpilot-tests-{Guid.NewGuid():N}"));
        await using var core = new SingBoxService(paths);
        var service = new SingBoxConfigService(paths, core);
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
}
