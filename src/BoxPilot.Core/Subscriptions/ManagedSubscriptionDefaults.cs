using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Subscriptions;

internal static class ManagedSubscriptionDefaults
{
    private const string AutomaticTag = "Auto";
    private const string TestUrl = "https://www.gstatic.com/generate_204";

    public static void Apply(JsonObject configuration)
    {
        if (configuration["outbounds"] is not JsonArray outbounds)
            return;

        var proxyTagList = outbounds
            .OfType<JsonObject>()
            .Where(static outbound => IsProxyType(JsonValueReader.String(outbound, "type")))
            .Select(static outbound => JsonValueReader.String(outbound, "tag"))
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var proxyTags = proxyTagList
            .ToHashSet(StringComparer.Ordinal);
        if (proxyTags.Count == 0)
            return;

        var selectors = outbounds
            .OfType<JsonObject>()
            .Where(static outbound => string.Equals(
                JsonValueReader.String(outbound, "type"),
                "selector",
                StringComparison.OrdinalIgnoreCase))
            .Select((outbound, index) => new
            {
                Outbound = outbound,
                Index = index,
                ProxyCount = CountProxyMembers(outbound, proxyTags),
            })
            .Where(static item => item.ProxyCount > 0)
            .OrderByDescending(static item => item.ProxyCount)
            .ThenBy(static item => item.Index)
            .ToArray();
        if (selectors.Length == 0)
            return;

        var automatic = outbounds
            .OfType<JsonObject>()
            .FirstOrDefault(outbound => IsUsableUrlTest(outbound, proxyTags));
        if (automatic is null)
        {
            var tag = AllocateTag(outbounds, AutomaticTag);
            automatic = new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = tag,
                ["outbounds"] = new JsonArray(
                    proxyTagList.Select(static item => JsonValue.Create(item)).ToArray()),
                ["url"] = TestUrl,
                ["interval"] = "5m",
                ["tolerance"] = 50,
                ["interrupt_exist_connections"] = true,
            };
            var insertionIndex = outbounds.IndexOf(selectors[0].Outbound);
            outbounds.Insert(insertionIndex, automatic);
        }

        var automaticTag = JsonValueReader.String(automatic, "tag");
        if (string.IsNullOrWhiteSpace(automaticTag))
            return;

        var primary = selectors[0].Outbound;
        var members = JsonValueReader.Array(primary, "outbounds") ?? [];
        primary["outbounds"] = new JsonArray(new[] { automaticTag }
            .Concat(members
                .Select(static item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item)
                               && !string.Equals(item, automaticTag, StringComparison.Ordinal)))
            .Select(static item => JsonValue.Create(item))
            .ToArray());
        primary["default"] = automaticTag;
        primary["interrupt_exist_connections"] = true;
    }

    private static bool IsProxyType(string? type)
    {
        return type is not null
               && type.ToLowerInvariant() is not (
                   "selector" or "urltest" or "direct" or "block" or "dns");
    }

    private static int CountProxyMembers(JsonObject outbound, IReadOnlySet<string> proxyTags)
    {
        return JsonValueReader.Array(outbound, "outbounds")?
            .Count(item => item is not null && proxyTags.Contains(item.ToString()))
            ?? 0;
    }

    private static bool IsUsableUrlTest(JsonObject outbound, IReadOnlySet<string> proxyTags)
    {
        return string.Equals(
                   JsonValueReader.String(outbound, "type"),
                   "urltest",
                   StringComparison.OrdinalIgnoreCase)
               && CountProxyMembers(outbound, proxyTags) > 0
               && !string.IsNullOrWhiteSpace(JsonValueReader.String(outbound, "tag"));
    }

    private static string AllocateTag(JsonArray outbounds, string requested)
    {
        var used = outbounds
            .OfType<JsonObject>()
            .Select(static outbound => JsonValueReader.String(outbound, "tag"))
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var candidate = requested;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = $"{requested} ({suffix++})";
        return candidate;
    }
}
