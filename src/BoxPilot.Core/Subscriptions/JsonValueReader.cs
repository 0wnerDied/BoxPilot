using System.Globalization;
using System.Text.Json.Nodes;

namespace BoxPilot.Core.Subscriptions;

internal static class JsonValueReader
{
    public static string? String(JsonObject source, params string[] names)
    {
        foreach (var name in names)
        {
            var node = source[name];
            if (node is null)
                continue;

            if (node is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
            return node.ToJsonString().Trim('"');
        }

        return null;
    }

    public static bool Boolean(JsonObject source, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (source[name] is not JsonValue value)
                continue;
            if (value.TryGetValue<bool>(out var boolean))
                return boolean;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
                return boolean;
        }

        return fallback;
    }

    public static int? Integer(JsonObject source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source[name] is not JsonValue value)
                continue;
            if (value.TryGetValue<int>(out var integer))
                return integer;
            if (value.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue)
                return (int)longValue;
            if (value.TryGetValue<string>(out var text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                return integer;
            }
        }

        return null;
    }

    public static JsonObject? Object(JsonObject source, params string[] names)
    {
        return names.Select(name => source[name]).OfType<JsonObject>().FirstOrDefault();
    }

    public static JsonArray? Array(JsonObject source, params string[] names)
    {
        return names.Select(name => source[name]).OfType<JsonArray>().FirstOrDefault();
    }

    public static JsonArray StringArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return new JsonArray(array
                .Select(item => item is null ? null : JsonValue.Create(item.ToString()))
                .Where(static item => item is not null)
                .ToArray());
        }

        var scalar = node?.ToString();
        return string.IsNullOrWhiteSpace(scalar) ? [] : new JsonArray(scalar);
    }

    public static void AddIfNotEmpty(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[name] = value;
    }

    public static void AddIfPresent(JsonObject target, string name, int? value)
    {
        if (value is not null)
            target[name] = value.Value;
    }
}
