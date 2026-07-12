using System.Text.Json.Nodes;

namespace BoxPilot.Core.Infrastructure;

internal static class JsonNodes
{
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
