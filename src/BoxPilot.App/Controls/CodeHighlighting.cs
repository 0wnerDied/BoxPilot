using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace BoxPilot.App.Controls;

internal static class CodeHighlighting
{
    private const string ResourcePrefix = "BoxPilot.App.Highlighting";
    private static readonly Lazy<IHighlightingDefinition> Json =
        new(() => Load("Json.xshd"));
    private static readonly Lazy<IHighlightingDefinition> Console =
        new(() => Load("Console.xshd"));

    public static IHighlightingDefinition GetDefinition(CodeLanguage language)
    {
        return language == CodeLanguage.Json ? Json.Value : Console.Value;
    }

    private static IHighlightingDefinition Load(string name)
    {
        var resourceName = $"{ResourcePrefix}.{name}";
        using var stream = typeof(CodeHighlighting).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing syntax definition {resourceName}.");
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
