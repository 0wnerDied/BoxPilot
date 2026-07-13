using System.Text.Json.Nodes;

namespace BoxPilot.Core.Infrastructure;

internal static class TunConfiguration
{
    public static async Task<bool> ContainsTunInboundAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var content = await Utf8Text.ReadAllTextAsync(
                Path.GetFullPath(configurationPath),
                cancellationToken)
            .ConfigureAwait(false);
        return ContainsTunInbound(content);
    }

    public static bool ContainsTunInbound(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        var root = JsonNode.Parse(configuration) as JsonObject
            ?? throw new InvalidDataException("The sing-box configuration root must be an object.");
        return root["inbounds"] is JsonArray inbounds
               && inbounds.OfType<JsonObject>().Any(static inbound => string.Equals(
                   inbound["type"]?.ToString(),
                   "tun",
                   StringComparison.OrdinalIgnoreCase));
    }
}
