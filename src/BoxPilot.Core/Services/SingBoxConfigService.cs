using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Services;

public sealed class SingBoxConfigService(
    AppPaths paths,
    SingBoxService singBoxService)
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public JsonObject Parse(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        try
        {
            return JsonNode.Parse(configuration, documentOptions: DocumentOptions) as JsonObject
                ?? throw new InvalidDataException("The sing-box configuration must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Invalid JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}: {exception.Message}",
                exception);
        }
    }

    public string Serialize(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ToJsonString(JsonDefaults.SerializerOptions) + Environment.NewLine;
    }

    public string FormatJson(string configuration)
    {
        return Serialize(Parse(configuration));
    }

    public JsonObject PrepareManagedSubscription(
        JsonObject configuration,
        string cacheId,
        bool preservePolicyGroups = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheId);

        var prepared = configuration.DeepClone().AsObject();
        if (!preservePolicyGroups)
            ManagedSubscriptionDefaults.Apply(prepared);
        var experimental = EnsureObject(prepared, "experimental");
        var cache = EnsureObject(experimental, "cache_file");
        cache["enabled"] = true;
        cache["cache_id"] = cacheId;
        return prepared;
    }

    public async Task<CommandResult> ValidateAsync(
        string configuration,
        CancellationToken cancellationToken = default)
    {
        var normalized = FormatJson(configuration);
        paths.EnsureCreated();
        var temporaryPath = Path.Combine(paths.RuntimeDirectory, $"validate-{Guid.NewGuid():N}.json");

        try
        {
            await AtomicFile.WriteAllTextAsync(temporaryPath, normalized, cancellationToken)
                .ConfigureAwait(false);
            return await singBoxService.CheckAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public JsonObject ApplyRuntimeOptions(JsonObject configuration, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settings);

        var clone = configuration.DeepClone().AsObject();
        var inbounds = EnsureArray(clone, "inbounds");
        var mixed = inbounds
            .OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(item["type"]?.GetValue<string>(), "mixed", StringComparison.Ordinal));

        if (mixed is null)
        {
            mixed = new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
            };
            inbounds.Insert(0, mixed);
        }

        mixed["listen_port"] = settings.MixedPort;
        mixed["set_system_proxy"] = settings.EnableSystemProxy;

        var tunInbounds = inbounds
            .OfType<JsonObject>()
            .Where(static item => string.Equals(
                item["type"]?.GetValue<string>(),
                "tun",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (settings.EnableTun && tunInbounds.Length == 0)
        {
            JsonNodes.Append(inbounds, new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "system",
            });
        }
        else if (!settings.EnableTun)
        {
            foreach (var tun in tunInbounds)
                inbounds.Remove(tun);
        }

        var experimental = EnsureObject(clone, "experimental");
        var cache = EnsureObject(experimental, "cache_file");
        cache["enabled"] = true;
        cache["path"] = Path.Combine(paths.CacheDirectory, "sing-box.db");

        var clashApi = EnsureObject(experimental, "clash_api");
        clashApi["external_controller"] = $"127.0.0.1:{settings.ClashApiPort}";
        clashApi["secret"] = settings.ClashApiSecret;

        var route = EnsureObject(clone, "route");
        StabilizeRemoteRuleSetDownloads(clone, route);
        route["auto_detect_interface"] = true;
        if (!OperatingSystem.IsAndroid())
        {
            // sing-box rejects this Android-only option before starting on desktop.
            route.Remove("override_android_vpn");
        }

        return clone;
    }

    private static void StabilizeRemoteRuleSetDownloads(JsonObject configuration, JsonObject route)
    {
        if (configuration["outbounds"] is not JsonArray outbounds
            || route["rule_set"] is not JsonArray ruleSets)
        {
            return;
        }

        var directTag = outbounds
            .OfType<JsonObject>()
            .FirstOrDefault(static outbound => string.Equals(
                outbound["type"]?.ToString(),
                "direct",
                StringComparison.OrdinalIgnoreCase))?["tag"]
            ?.ToString();
        if (string.IsNullOrWhiteSpace(directTag))
            return;

        var dynamicOutbounds = outbounds
            .OfType<JsonObject>()
            .Where(static outbound =>
            {
                var type = outbound["type"]?.ToString();
                return string.Equals(type, "selector", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(type, "urltest", StringComparison.OrdinalIgnoreCase);
            })
            .Select(static outbound => outbound["tag"]?.ToString())
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var ruleSet in ruleSets.OfType<JsonObject>())
        {
            if (!string.Equals(
                    ruleSet["type"]?.ToString(),
                    "remote",
                    StringComparison.OrdinalIgnoreCase)
                || ruleSet["http_client"] is not null)
            {
                continue;
            }

            var detour = ruleSet["download_detour"]?.ToString();
            if (detour is not null && dynamicOutbounds.Contains(detour))
            {
                // Remote rules initialize before selector health checks and persisted choices.
                // A group detour can therefore make startup depend on an unavailable first node.
                ruleSet["download_detour"] = directTag;
            }
        }
    }

    public JsonObject CreateStarterConfiguration(AppSettings settings)
    {
        var configuration = SingBoxConfigurationBuilder.CreateBaseConfiguration(
            new JsonArray(
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" }),
            "direct",
            dnsStrategy: null);
        return ApplyRuntimeOptions(configuration, settings);
    }

    private static JsonArray EnsureArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray value)
            return value;

        value = [];
        parent[propertyName] = value;
        return value;
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject value)
            return value;

        value = [];
        parent[propertyName] = value;
        return value;
    }
}
