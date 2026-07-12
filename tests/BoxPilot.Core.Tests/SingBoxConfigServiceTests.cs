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
}
