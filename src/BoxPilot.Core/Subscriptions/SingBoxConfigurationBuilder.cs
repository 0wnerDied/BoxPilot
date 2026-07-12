using System.Text.Json.Nodes;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Subscriptions;

internal sealed class SingBoxConfigurationBuilder(SingBoxConfigService configService)
{
    public JsonObject Build(
        IReadOnlyList<JsonObject> sourceOutbounds,
        SubscriptionBuildOptions options,
        ICollection<string> warnings)
    {
        if (sourceOutbounds.Count == 0)
            throw new InvalidDataException("The subscription did not contain any supported proxy nodes.");

        var allocator = new TagAllocator();
        var outbounds = new JsonArray();
        var nodeTags = new JsonArray();

        foreach (var source in sourceOutbounds)
        {
            var outbound = source.DeepClone().AsObject();
            var sourceTag = JsonValueReader.String(outbound, "tag") ?? "Proxy";
            var tag = allocator.Allocate(sourceTag);
            if (!string.Equals(sourceTag, tag, StringComparison.Ordinal))
                warnings.Add($"Renamed duplicate proxy tag '{sourceTag}' to '{tag}'.");
            outbound["tag"] = tag;
            outbounds.Add(outbound);
            nodeTags.Add(tag);
        }

        outbounds.Add(new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = "Auto",
            ["outbounds"] = nodeTags.DeepClone(),
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "3m",
            ["tolerance"] = 50,
        });
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = "Proxy",
            ["outbounds"] = new JsonArray(new[] { JsonValue.Create("Auto") }
                .Concat(nodeTags.Select(static node => node?.DeepClone()))
                .ToArray()),
            ["default"] = "Auto",
            ["interrupt_exist_connections"] = true,
        });
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "block" });

        var configuration = CreateBaseConfiguration(outbounds, "Proxy");
        return configService.ApplyRuntimeOptions(configuration, ToSettings(options));
    }

    public static JsonObject CreateBaseConfiguration(JsonArray outbounds, string finalOutbound)
    {
        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "local", ["tag"] = "dns-local" },
                },
                ["final"] = "dns-local",
                ["strategy"] = "prefer_ipv4",
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                },
                ["final"] = finalOutbound,
                ["auto_detect_interface"] = true,
            },
        };
    }

    internal static AppSettings ToSettings(SubscriptionBuildOptions options)
    {
        return new AppSettings
        {
            MixedPort = options.MixedPort,
            ClashApiPort = options.ClashApiPort,
            ClashApiSecret = options.ClashApiSecret,
            EnableSystemProxy = options.EnableSystemProxy,
            EnableTun = options.EnableTun,
        };
    }

    internal sealed class TagAllocator
    {
        private readonly HashSet<string> used = new(StringComparer.Ordinal);

        public string Allocate(string? requested)
        {
            var baseName = string.IsNullOrWhiteSpace(requested) ? "Proxy" : requested.Trim();
            var candidate = baseName;
            var suffix = 2;
            while (!used.Add(candidate))
                candidate = $"{baseName} ({suffix++})";
            return candidate;
        }
    }
}
