using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Controls;

public sealed class AnsiTextBlock : TextBlock
{
    public static readonly StyledProperty<AnsiTextDocument?> DocumentProperty =
        AvaloniaProperty.Register<AnsiTextBlock, AnsiTextDocument?>(nameof(Document));

    static AnsiTextBlock()
    {
        DocumentProperty.Changed.AddClassHandler<AnsiTextBlock>(
            static (control, _) => control.ApplyDocument());
    }

    public AnsiTextDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void ApplyDocument()
    {
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        if (Document is not { } document)
            return;

        var position = 0;
        foreach (var span in document.Spans)
        {
            var start = Math.Clamp(span.Start, position, document.Text.Length);
            var end = Math.Clamp(span.Start + span.Length, start, document.Text.Length);
            if (start > position)
                inlines.Add(document.Text[position..start]);
            if (end > start)
            {
                var run = new Run(document.Text[start..end]);
                AnsiBrushPalette.Apply(run, span.Style);
                inlines.Add(run);
            }
            position = end;
        }

        if (position < document.Text.Length)
            inlines.Add(document.Text[position..]);
    }
}
