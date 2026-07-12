using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Tests;

public sealed class CommandLineTokenizerTests
{
    [Fact]
    public void SplitPreservesQuotedArguments()
    {
        var arguments = CommandLineTokenizer.Split(
            "merge output.json -c 'first profile.json' -c \"second profile.json\"");

        Assert.Equal(
            ["merge", "output.json", "-c", "first profile.json", "-c", "second profile.json"],
            arguments);
    }

    [Fact]
    public void SplitRejectsUnterminatedQuotes()
    {
        Assert.Throws<FormatException>(() => CommandLineTokenizer.Split("check -c 'broken"));
    }
}
