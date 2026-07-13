using System.Text.Json.Nodes;

namespace BoxPilot.Core.Infrastructure;

internal static class JsonNodes
{
    public static JsonArray EnsureArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray value)
            return value;

        value = [];
        parent[propertyName] = value;
        return value;
    }

    public static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject value)
            return value;

        value = [];
        parent[propertyName] = value;
        return value;
    }

    // Keep callers on the trim-safe JsonNode overload instead of generic Add<T>.
    public static void Append(JsonArray target, JsonNode value)
    {
        target.Add(value);
    }

    public static void Append(JsonArray target, string value)
    {
        target.Add((JsonNode?)JsonValue.Create(value));
    }
}
