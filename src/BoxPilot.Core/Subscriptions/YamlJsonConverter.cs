using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace BoxPilot.Core.Subscriptions;

internal static class YamlJsonConverter
{
    public static JsonObject ParseObject(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            throw new InvalidDataException("The YAML subscription is empty.");

        return ConvertNode(stream.Documents[0].RootNode) as JsonObject
            ?? throw new InvalidDataException("The YAML subscription must contain a mapping at its root.");
    }

    private static JsonNode? ConvertNode(YamlNode node)
    {
        return node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ConvertNode).ToArray()),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => JsonValue.Create(node.ToString()),
        };
    }

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value
                ?? throw new InvalidDataException("YAML mapping keys must be scalar values.");
            result[key] = ConvertNode(pair.Value);
        }

        return result;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        var tag = scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific ? string.Empty : scalar.Tag.Value;
        if (value is null || tag.EndsWith(":null", StringComparison.Ordinal))
            return null;
        if (tag.EndsWith(":str", StringComparison.Ordinal)
            || scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted
                or YamlDotNet.Core.ScalarStyle.DoubleQuoted
                or YamlDotNet.Core.ScalarStyle.Literal
                or YamlDotNet.Core.ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }
        if (bool.TryParse(value, out var boolean))
            return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) || value == "~")
            return null;

        return JsonValue.Create(value);
    }
}
