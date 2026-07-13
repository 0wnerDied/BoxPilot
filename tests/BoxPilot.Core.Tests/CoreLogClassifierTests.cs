using System.Globalization;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Tests;

public sealed class CoreLogClassifierTests
{
    [Theory]
    [InlineData("+0800 2026-07-12 20:14:22 INFO[0000] connected", CoreLogLevel.Information)]
    [InlineData("WARN[0001] retrying", CoreLogLevel.Warning)]
    [InlineData("ERROR failed to create TUN", CoreLogLevel.Error)]
    [InlineData("FATAL permission denied", CoreLogLevel.Fatal)]
    [InlineData("DEBUG route match", CoreLogLevel.Debug)]
    public void DetectUsesSemanticLevelInsteadOfOutputStream(
        string message,
        CoreLogLevel expected)
    {
        var level = CoreLogClassifier.Detect(CoreLogStream.StandardError, message);

        Assert.Equal(expected, level);
    }

    [Fact]
    public void DetectLabelsApplicationMessagesAsSystem()
    {
        var level = CoreLogClassifier.Detect(CoreLogStream.BoxPilot, "sing-box stopped.");

        Assert.Equal(CoreLogLevel.System, level);
    }

    [Fact]
    public void ParseSeparatesSingBoxTimestampAndLevelFromStyledMessage()
    {
        const string message = "+0800 2026-07-12 20:14:22 \u001B[36mINFO\u001B[0m "
                               + "[\u001B[38;5;96m456430416\u001B[0m 1ms] connected";

        var entry = CoreLogParser.Parse(
            DateTimeOffset.Parse("2026-07-12T20:14:22+08:00", CultureInfo.InvariantCulture),
            CoreLogStream.StandardError,
            message);

        Assert.Equal(CoreLogLevel.Information, entry.Level);
        Assert.Equal("[456430416 1ms] connected", entry.Message);
        var span = Assert.Single(entry.Content.Spans);
        Assert.Equal("456430416", entry.Message.Substring(span.Start, span.Length));
    }
}
