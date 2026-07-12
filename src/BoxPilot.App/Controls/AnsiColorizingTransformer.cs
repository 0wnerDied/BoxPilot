using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using BoxPilot.Core.Models;

namespace BoxPilot.App.Controls;

internal sealed class AnsiColorizingTransformer : DocumentColorizingTransformer
{
    public AnsiTextDocument Document { get; set; } = AnsiTextDocument.Plain(string.Empty);

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var span in Document.Spans)
        {
            var start = Math.Max(span.Start, line.Offset);
            var end = Math.Min(span.Start + span.Length, line.EndOffset);
            if (end <= start)
                continue;

            ChangeLinePart(start, end, element => AnsiBrushPalette.Apply(element, span.Style));
        }
    }
}
