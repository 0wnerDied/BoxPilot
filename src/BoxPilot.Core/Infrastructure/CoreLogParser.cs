using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

public static class CoreLogParser
{
    public static CoreLogEntry Parse(
        DateTimeOffset timestamp,
        CoreLogStream stream,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = AnsiTextParser.Parse(message);
        var level = CoreLogClassifier.Detect(stream, content.Text);
        return new CoreLogEntry(
            timestamp,
            stream,
            level,
            message,
            TrimSingBoxPrefix(content));
    }

    private static AnsiTextDocument TrimSingBoxPrefix(AnsiTextDocument content)
    {
        var value = content.Text.AsSpan();
        if (!HasTimestampPrefix(value))
            return content;

        var index = 26;
        while (index < value.Length && char.IsAsciiLetter(value[index]))
            index++;
        if (index == 26)
            return content;
        while (index < value.Length && value[index] == ' ')
            index++;
        return content.Slice(index);
    }

    private static bool HasTimestampPrefix(ReadOnlySpan<char> value)
    {
        if (value.Length < 26 || value[0] is not ('+' or '-'))
            return false;

        ReadOnlySpan<int> digits = [1, 2, 3, 4, 6, 7, 8, 9, 11, 12, 14, 15, 17, 18, 20, 21, 23, 24];
        foreach (var index in digits)
        {
            if (!char.IsAsciiDigit(value[index]))
                return false;
        }

        return value[5] == ' '
               && value[10] == '-'
               && value[13] == '-'
               && value[16] == ' '
               && value[19] == ':'
               && value[22] == ':'
               && value[25] == ' ';
    }
}
