using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Controls;

internal static class AnsiBrushPalette
{
    private static readonly AnsiColor DefaultForeground = new(232, 225, 214);
    private static readonly AnsiColor DefaultBackground = new(33, 30, 26);
    private static readonly Dictionary<(AnsiColor Color, byte Alpha), IBrush> Brushes = [];
    private static readonly object BrushGate = new();

    public static void Apply(Run run, AnsiTextStyle style)
    {
        var (foreground, background) = ResolveColors(style);
        if (foreground is not null)
            run.Foreground = GetBrush(foreground.Value, style.Faint ? (byte)155 : (byte)255);
        if (background is not null)
            run.Background = GetBrush(background.Value, 255);
        if (style.Bold)
            run.FontWeight = FontWeight.Bold;
        if (style.Italic)
            run.FontStyle = FontStyle.Italic;
        if (style.Underline)
            run.TextDecorations = TextDecorations.Underline;
    }

    public static void Apply(VisualLineElement element, AnsiTextStyle style)
    {
        var properties = element.TextRunProperties;
        var (foreground, background) = ResolveColors(style);
        if (foreground is not null)
        {
            properties.SetForegroundBrush(GetBrush(
                foreground.Value,
                style.Faint ? (byte)155 : (byte)255));
        }
        if (background is not null)
            properties.SetBackgroundBrush(GetBrush(background.Value, 255));
        if (style.Bold || style.Italic)
        {
            var typeface = properties.Typeface;
            properties.SetTypeface(new Typeface(
                typeface.FontFamily,
                style.Italic ? FontStyle.Italic : typeface.Style,
                style.Bold ? FontWeight.Bold : typeface.Weight,
                typeface.Stretch));
        }
        if (style.Underline)
            properties.SetTextDecorations(TextDecorations.Underline);
    }

    private static (AnsiColor? Foreground, AnsiColor? Background) ResolveColors(
        AnsiTextStyle style)
    {
        if (style.Inverse)
        {
            return (
                style.Background ?? DefaultBackground,
                style.Foreground ?? DefaultForeground);
        }

        var foreground = style.Foreground;
        if (style.Faint && foreground is null)
            foreground = DefaultForeground;
        return (foreground, style.Background);
    }

    private static IBrush GetBrush(AnsiColor color, byte alpha)
    {
        var key = (color, alpha);
        lock (BrushGate)
        {
            if (Brushes.TryGetValue(key, out var brush))
                return brush;

            brush = new SolidColorBrush(Color.FromArgb(alpha, color.Red, color.Green, color.Blue));
            if (Brushes.Count < 256)
                Brushes[key] = brush;
            return brush;
        }
    }
}
