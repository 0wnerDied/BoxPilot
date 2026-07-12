namespace BoxPilot.Core.Models;

public readonly record struct AnsiColor(byte Red, byte Green, byte Blue);

public readonly record struct AnsiTextStyle(
    AnsiColor? Foreground,
    AnsiColor? Background,
    bool Bold,
    bool Faint,
    bool Italic,
    bool Underline,
    bool Inverse)
{
    public static AnsiTextStyle Default { get; } = new();
}

public sealed record AnsiTextSpan(int Start, int Length, AnsiTextStyle Style);

public sealed record AnsiTextDocument(string Text, IReadOnlyList<AnsiTextSpan> Spans)
{
    public static AnsiTextDocument Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new AnsiTextDocument(text, []);
    }

    public AnsiTextDocument Slice(int start)
    {
        if (start < 0 || start > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (start == 0)
            return this;

        var spans = Spans
            .Where(span => span.Start + span.Length > start)
            .Select(span =>
            {
                var spanStart = Math.Max(span.Start, start);
                return new AnsiTextSpan(
                    spanStart - start,
                    span.Start + span.Length - spanStart,
                    span.Style);
            })
            .ToArray();
        return new AnsiTextDocument(Text[start..], spans);
    }
}
