using System.Globalization;
using System.Text;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

public static class AnsiTextParser
{
    private const char Escape = '\u001B';
    private const char StringTerminator = '\u009C';

    public static AnsiTextDocument Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var firstControl = FindFirstControl(value);
        if (firstControl < 0)
            return AnsiTextDocument.Plain(value);

        var text = new StringBuilder(value.Length);
        text.Append(value, 0, firstControl);
        var spans = new List<AnsiTextSpan>();
        var style = default(AnsiTextStyle);
        var styleStart = text.Length;
        var index = firstControl;
        while (index < value.Length)
        {
            var character = value[index];
            if (character == Escape)
            {
                index = ReadEscape(value, index + 1, text, spans, ref style, ref styleStart);
                continue;
            }

            if (character == '\u009B')
            {
                index = ReadControlSequence(
                    value,
                    index + 1,
                    text,
                    spans,
                    ref style,
                    ref styleStart);
                continue;
            }

            if (character is '\u0090' or '\u0098' or '\u009D' or '\u009E' or '\u009F')
            {
                index = SkipControlString(value, index + 1);
                continue;
            }

            if (character is not ('\t' or '\r' or '\n') && char.IsControl(character))
            {
                index++;
                continue;
            }

            text.Append(character);
            index++;
        }

        AddSpan(spans, style, styleStart, text.Length);
        return new AnsiTextDocument(text.ToString(), spans);
    }

    private static int FindFirstControl(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is not ('\t' or '\r' or '\n') && char.IsControl(value[index]))
                return index;
        }

        return -1;
    }

    private static int ReadEscape(
        string value,
        int index,
        StringBuilder text,
        ICollection<AnsiTextSpan> spans,
        ref AnsiTextStyle style,
        ref int styleStart)
    {
        if (index >= value.Length)
            return index;

        return value[index] switch
        {
            '[' => ReadControlSequence(
                value,
                index + 1,
                text,
                spans,
                ref style,
                ref styleStart),
            ']' or 'P' or 'X' or '^' or '_' => SkipControlString(value, index + 1),
            _ => SkipSingleEscapeSequence(value, index),
        };
    }

    private static int ReadControlSequence(
        string value,
        int index,
        StringBuilder text,
        ICollection<AnsiTextSpan> spans,
        ref AnsiTextStyle style,
        ref int styleStart)
    {
        var parameterStart = index;
        while (index < value.Length)
        {
            var character = value[index];
            if (character is >= '@' and <= '~')
            {
                if (character == 'm')
                {
                    AddSpan(spans, style, styleStart, text.Length);
                    ApplyGraphicsRendition(value.AsSpan(parameterStart, index - parameterStart), ref style);
                    styleStart = text.Length;
                }

                return index + 1;
            }

            if (character is < ' ' or > '?')
                return index;
            index++;
        }

        return index;
    }

    private static void ApplyGraphicsRendition(ReadOnlySpan<char> value, ref AnsiTextStyle style)
    {
        Span<int> parameters = stackalloc int[32];
        var count = ParseParameters(value, parameters);
        if (count == 0)
        {
            style = default;
            return;
        }

        for (var index = 0; index < count; index++)
        {
            var code = parameters[index];
            switch (code)
            {
                case 0:
                    style = default;
                    break;
                case 1:
                    style = style with { Bold = true };
                    break;
                case 2:
                    style = style with { Faint = true };
                    break;
                case 3:
                    style = style with { Italic = true };
                    break;
                case 4:
                    style = style with { Underline = true };
                    break;
                case 7:
                    style = style with { Inverse = true };
                    break;
                case 22:
                    style = style with { Bold = false, Faint = false };
                    break;
                case 23:
                    style = style with { Italic = false };
                    break;
                case 24:
                    style = style with { Underline = false };
                    break;
                case 27:
                    style = style with { Inverse = false };
                    break;
                case >= 30 and <= 37:
                    style = style with { Foreground = StandardColor(code - 30, false) };
                    break;
                case 38:
                    style = style with
                    {
                        Foreground = ReadExtendedColor(parameters[..count], ref index)
                                     ?? style.Foreground,
                    };
                    break;
                case 39:
                    style = style with { Foreground = null };
                    break;
                case >= 40 and <= 47:
                    style = style with { Background = StandardColor(code - 40, false) };
                    break;
                case 48:
                    style = style with
                    {
                        Background = ReadExtendedColor(parameters[..count], ref index)
                                     ?? style.Background,
                    };
                    break;
                case 49:
                    style = style with { Background = null };
                    break;
                case >= 90 and <= 97:
                    style = style with { Foreground = StandardColor(code - 90, true) };
                    break;
                case >= 100 and <= 107:
                    style = style with { Background = StandardColor(code - 100, true) };
                    break;
            }
        }
    }

    private static int ParseParameters(ReadOnlySpan<char> value, Span<int> destination)
    {
        if (value.IsEmpty)
            return 0;

        var count = 0;
        var start = 0;
        for (var index = 0; index <= value.Length && count < destination.Length; index++)
        {
            if (index < value.Length && value[index] != ';')
                continue;

            var parameter = value[start..index];
            destination[count++] = int.TryParse(
                parameter,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0;
            start = index + 1;
        }

        return count;
    }

    private static AnsiColor? ReadExtendedColor(ReadOnlySpan<int> parameters, ref int index)
    {
        if (index + 2 < parameters.Length && parameters[index + 1] == 5)
        {
            index += 2;
            return IndexedColor(Math.Clamp(parameters[index], 0, 255));
        }

        if (index + 4 < parameters.Length && parameters[index + 1] == 2)
        {
            var color = new AnsiColor(
                (byte)Math.Clamp(parameters[index + 2], 0, 255),
                (byte)Math.Clamp(parameters[index + 3], 0, 255),
                (byte)Math.Clamp(parameters[index + 4], 0, 255));
            index += 4;
            return color;
        }

        return null;
    }

    private static AnsiColor IndexedColor(int index)
    {
        if (index < 16)
            return StandardColor(index % 8, index >= 8);
        if (index >= 232)
        {
            var level = (byte)(8 + ((index - 232) * 10));
            return new AnsiColor(level, level, level);
        }

        index -= 16;
        return new AnsiColor(
            ColorCubeLevel(index / 36),
            ColorCubeLevel((index / 6) % 6),
            ColorCubeLevel(index % 6));
    }

    private static byte ColorCubeLevel(int value)
    {
        return value == 0 ? (byte)0 : (byte)(55 + (value * 40));
    }

    private static AnsiColor StandardColor(int index, bool bright)
    {
        return (index, bright) switch
        {
            (0, false) => new AnsiColor(35, 31, 28),
            (1, false) => new AnsiColor(205, 84, 76),
            (2, false) => new AnsiColor(110, 154, 108),
            (3, false) => new AnsiColor(190, 137, 78),
            (4, false) => new AnsiColor(91, 126, 158),
            (5, false) => new AnsiColor(159, 111, 157),
            (6, false) => new AnsiColor(83, 151, 157),
            (7, false) => new AnsiColor(205, 198, 188),
            (0, true) => new AnsiColor(111, 104, 96),
            (1, true) => new AnsiColor(224, 108, 117),
            (2, true) => new AnsiColor(152, 195, 121),
            (3, true) => new AnsiColor(229, 192, 123),
            (4, true) => new AnsiColor(97, 175, 239),
            (5, true) => new AnsiColor(198, 120, 221),
            (6, true) => new AnsiColor(86, 182, 194),
            _ => new AnsiColor(242, 236, 226),
        };
    }

    private static int SkipControlString(string value, int index)
    {
        while (index < value.Length)
        {
            if (value[index] == '\a' || value[index] == StringTerminator)
                return index + 1;
            if (value[index] == Escape
                && index + 1 < value.Length
                && value[index + 1] == '\\')
            {
                return index + 2;
            }

            index++;
        }

        return index;
    }

    private static int SkipSingleEscapeSequence(string value, int index)
    {
        while (index < value.Length && value[index] is >= ' ' and <= '/')
            index++;
        return index < value.Length && value[index] is >= '0' and <= '~'
            ? index + 1
            : index;
    }

    private static void AddSpan(
        ICollection<AnsiTextSpan> spans,
        AnsiTextStyle style,
        int start,
        int end)
    {
        if (end > start && style != default)
            spans.Add(new AnsiTextSpan(start, end - start, style));
    }
}
