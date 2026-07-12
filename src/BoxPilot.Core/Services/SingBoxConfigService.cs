using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

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

        var tun = inbounds
            .OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(item["type"]?.GetValue<string>(), "tun", StringComparison.Ordinal));
        if (settings.EnableTun && tun is null)
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
        else if (!settings.EnableTun && tun is not null && string.Equals(
                     tun["tag"]?.GetValue<string>(),
                     "tun-in",
                     StringComparison.Ordinal))
        {
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
        route["auto_detect_interface"] = true;

        return clone;
    }

    public JsonObject CreateStarterConfiguration(AppSettings settings)
    {
        var configuration = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "local",
                        ["tag"] = "dns-local",
                    }),
                ["final"] = "dns-local",
            },
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }),
                ["final"] = "direct",
                ["auto_detect_interface"] = true,
            },
        };

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
