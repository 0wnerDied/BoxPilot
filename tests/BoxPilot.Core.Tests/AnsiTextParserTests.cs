using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Tests;

public sealed class AnsiTextParserTests
{
    [Fact]
    public void ParseRendersStandardAndIndexedColorsWithoutEscapeText()
    {
        const string value = "\u001B[31mFATAL\u001B[0m[0000] "
                             + "\u001B[38;5;96mIPv6 日本 A01\u001B[0m";

        var document = AnsiTextParser.Parse(value);

        Assert.Equal("FATAL[0000] IPv6 日本 A01", document.Text);
        Assert.Equal(2, document.Spans.Count);
        Assert.Equal(new AnsiColor(205, 84, 76), document.Spans[0].Style.Foreground);
        Assert.Equal("FATAL", Slice(document, document.Spans[0]));
        Assert.Equal(new AnsiColor(135, 95, 135), document.Spans[1].Style.Foreground);
        Assert.Equal("IPv6 日本 A01", Slice(document, document.Spans[1]));
    }

    [Fact]
    public void ParseRendersTrueColorBackgroundAndFontAttributes()
    {
        const string value = "\u001B[1;3;4;38;2;1;2;3;48;5;235mstyled\u001B[0m plain";

        var document = AnsiTextParser.Parse(value);

        var span = Assert.Single(document.Spans);
        Assert.Equal("styled plain", document.Text);
        Assert.Equal(new AnsiColor(1, 2, 3), span.Style.Foreground);
        Assert.Equal(new AnsiColor(38, 38, 38), span.Style.Background);
        Assert.True(span.Style.Bold);
        Assert.True(span.Style.Italic);
        Assert.True(span.Style.Underline);
        Assert.Equal("styled", Slice(document, span));
    }

    [Fact]
    public void ParsePreservesLinkLabelAndUnicodeWhileRemovingOscEnvelope()
    {
        const string value = "\u001B]8;;https://example.com\u001B\\日本节点\u001B]8;;\a 可用";

        var document = AnsiTextParser.Parse(value);

        Assert.Equal("日本节点 可用", document.Text);
        Assert.Empty(document.Spans);
    }

    [Fact]
    public void ParseReturnsOriginalTextWhenNoTerminalCodesExist()
    {
        const string value = "plain UTF-8 日本语";

        var document = AnsiTextParser.Parse(value);

        Assert.Same(value, document.Text);
        Assert.Empty(document.Spans);
    }

    [Fact]
    public void CommandResultKeepsTerminalFormattingForRenderersAndPlainTextForErrors()
    {
        var result = new CommandResult(
            1,
            string.Empty,
            "\u001B[31mFATAL\u001B[0m permission denied");

        Assert.Contains("\u001B[31m", result.CombinedTerminalOutput);
        Assert.Equal("FATAL permission denied", result.CombinedOutput);
    }

    private static string Slice(AnsiTextDocument document, AnsiTextSpan span)
    {
        return document.Text.Substring(span.Start, span.Length);
    }
}
