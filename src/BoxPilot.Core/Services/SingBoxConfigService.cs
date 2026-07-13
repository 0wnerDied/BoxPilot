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
    public const string ManagedGlobalSelectorTag = "boxpilot-global";

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

    public ClashApiConnection? GetClashApiConnection(string configuration)
    {
        return GetClashApiConnection(Parse(configuration));
    }

    public ClashApiConnection? GetClashApiConnection(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clashApi = configuration["experimental"]?["clash_api"] as JsonObject;
        var controller = clashApi?["external_controller"]?.ToString();
        if (controller?.StartsWith(':') == true)
            controller = "127.0.0.1" + controller;
        if (string.IsNullOrWhiteSpace(controller)
            || !Uri.TryCreate($"http://{controller}", UriKind.Absolute, out var endpoint)
            || endpoint.Port is <= 0 or > 65_535)
        {
            return null;
        }

        return new ClashApiConnection(
            endpoint.Port,
            clashApi?["secret"]?.ToString() ?? string.Empty);
    }

    public bool SupportsStandardRoutingModes(string configuration)
    {
        return SupportsStandardRoutingModes(Parse(configuration));
    }

    public bool CanAddStandardRoutingModes(string configuration)
    {
        return CanAddStandardRoutingModes(Parse(configuration));
    }

    public bool CanAddStandardRoutingModes(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SupportsStandardRoutingModes(configuration)
            || configuration["outbounds"] is not JsonArray outbounds)
        {
            return false;
        }

        var outboundObjects = outbounds.OfType<JsonObject>().ToArray();
        var route = configuration["route"] as JsonObject;
        return FindGlobalOutbound(outboundObjects, route, includeManaged: false) is not null;
    }

    public JsonObject AddStandardRoutingModes(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var clone = configuration.DeepClone().AsObject();
        if (SupportsStandardRoutingModes(clone))
            return clone;
        return EnsureManagedStandardRoutingModesCore(clone);
    }

    public JsonObject EnsureManagedStandardRoutingModes(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return EnsureManagedStandardRoutingModesCore(configuration.DeepClone().AsObject());
    }

    public string? GetGlobalProxyGroup(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var target = (configuration["route"]?["rules"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(static rule => string.Equals(
                rule["clash_mode"]?.ToString(),
                "global",
                StringComparison.OrdinalIgnoreCase))?["outbound"]?.ToString();
        if (configuration["outbounds"] is not JsonArray outbounds
            || FindOutbound(outbounds.OfType<JsonObject>().ToArray(), target) is not { } outbound
            || outbound["type"]?.ToString() is not { } type
            || type.ToLowerInvariant() is not ("selector" or "urltest"))
        {
            return null;
        }

        return target;
    }

    public static bool IsManagedGlobalSelector(string? tag)
    {
        return tag is not null
               && (string.Equals(tag, ManagedGlobalSelectorTag, StringComparison.Ordinal)
                   || tag.StartsWith(ManagedGlobalSelectorTag + " (", StringComparison.Ordinal));
    }

    private JsonObject EnsureManagedStandardRoutingModesCore(JsonObject clone)
    {
        if (clone["outbounds"] is not JsonArray outbounds)
        {
            throw new InvalidDataException(
                "Standard routing modes require at least one proxy outbound.");
        }

        var outboundObjects = outbounds.OfType<JsonObject>().ToArray();
        var route = EnsureObject(clone, "route");
        var globalSource = FindGlobalOutbound(outboundObjects, route, includeManaged: false)
                           ?? throw new InvalidDataException(
                               "Standard routing modes require at least one proxy outbound.");
        var rules = route["rules"] as JsonArray;
        var needsDirect = rules is null || !ContainsRoutingMode(rules, "direct");
        var direct = string.Empty;
        if (needsDirect)
        {
            direct = FindDirectOutbound(outboundObjects) ?? string.Empty;
            if (direct.Length == 0)
            {
                var allocator = new SingBoxConfigurationBuilder.TagAllocator(outboundObjects
                    .Select(static outbound => outbound["tag"]?.ToString())
                    .OfType<string>());
                direct = allocator.Allocate("boxpilot-direct");
                JsonNodes.Append(outbounds, new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = direct,
                });
            }
        }

        var managedGlobal = outboundObjects.FirstOrDefault(static outbound =>
            string.Equals(outbound["type"]?.ToString(), "selector", StringComparison.OrdinalIgnoreCase)
            && IsManagedGlobalSelector(outbound["tag"]?.ToString()));
        if (managedGlobal is null)
        {
            var allocator = new SingBoxConfigurationBuilder.TagAllocator(outboundObjects
                .Select(static outbound => outbound["tag"]?.ToString())
                .OfType<string>());
            managedGlobal = new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = allocator.Allocate(ManagedGlobalSelectorTag),
            };
            JsonNodes.Append(outbounds, managedGlobal);
        }

        var managedGlobalTag = managedGlobal["tag"]!.ToString();
        var members = BuildGlobalSelectorMembers(
            outbounds.OfType<JsonObject>().ToArray(),
            globalSource,
            managedGlobalTag);
        if (members.Count == 0)
            throw new InvalidDataException("No proxy nodes are available for Global mode.");
        var defaultMember = managedGlobal["default"]?.ToString();
        var sourceDefault = FindOutbound(outboundObjects, globalSource)?["default"]?.ToString();
        if (defaultMember is null || !members.Contains(defaultMember, StringComparer.Ordinal))
        {
            defaultMember = sourceDefault is not null
                            && members.Contains(sourceDefault, StringComparer.Ordinal)
                ? sourceDefault
                : members[0];
        }
        managedGlobal["outbounds"] = new JsonArray(
            members.Select(static member => JsonValue.Create(member)).ToArray());
        managedGlobal["default"] = defaultMember;
        managedGlobal["interrupt_exist_connections"] = true;

        ApplyRoutingModeRules(route, direct, managedGlobalTag, replaceGlobal: true);
        if (!SupportsStandardRoutingModes(clone))
            throw new InvalidDataException("Standard routing modes could not be added safely.");
        return clone;
    }

    public bool SupportsStandardRoutingModes(JsonObject configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["route"]?["rules"] is not JsonArray rules)
            return false;

        var modes = rules.OfType<JsonObject>()
            .Select(static rule => rule["clash_mode"]?.ToString())
            .Where(static mode => !string.IsNullOrWhiteSpace(mode))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return modes.Contains("direct") && modes.Contains("global");
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
        return await ValidateAsync(configuration, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> MergeAsync(
        IReadOnlyList<string> configurationPaths,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configurationPaths);
        if (configurationPaths.Count == 0)
            throw new ArgumentException("At least one configuration is required.", nameof(configurationPaths));

        paths.EnsureCreated();
        var temporaryPath = Path.Combine(paths.RuntimeDirectory, $"merge-{Guid.NewGuid():N}.json");
        try
        {
            var result = await singBoxService.MergeAsync(
                    configurationPaths,
                    temporaryPath,
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
                throw new InvalidDataException(result.CombinedOutput);
            return await Utf8Text.ReadAllTextAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<CommandResult> ValidateAsync(
        string configuration,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var normalized = FormatJson(configuration);
        paths.EnsureCreated();
        var temporaryPath = Path.Combine(paths.RuntimeDirectory, $"validate-{Guid.NewGuid():N}.json");

        try
        {
            await AtomicFile.WriteAllTextAsync(temporaryPath, normalized, cancellationToken)
                .ConfigureAwait(false);
            return await singBoxService.CheckAsync(
                    temporaryPath,
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
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
        mixed["listen"] = settings.AllowLan ? "0.0.0.0" : "127.0.0.1";
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
        clashApi["default_mode"] = NormalizeRoutingMode(settings.RoutingMode);

        var route = EnsureObject(clone, "route");
        StabilizeRemoteRuleSetDownloads(clone, route);
        route["auto_detect_interface"] = true;
        if (!OperatingSystem.IsAndroid())
        {
            // sing-box rejects this Android-only option before starting on desktop.
            route.Remove("override_android_vpn");
        }

        ApplyCustomDns(clone, settings.CustomDnsServer);

        return clone;
    }

    private static void ApplyRoutingModeRules(
        JsonObject route,
        string direct,
        string global,
        bool replaceGlobal)
    {
        var rules = EnsureArray(route, "rules");
        var insertionIndex = FindInitialRuleIndex(rules);
        if (!ContainsRoutingMode(rules, "direct"))
        {
            rules.Insert(insertionIndex++, new JsonObject
            {
                ["clash_mode"] = "direct",
                ["action"] = "route",
                ["outbound"] = direct,
            });
        }
        var globalRule = rules.OfType<JsonObject>().FirstOrDefault(static rule => string.Equals(
            rule["clash_mode"]?.ToString(),
            "global",
            StringComparison.OrdinalIgnoreCase));
        if (globalRule is null)
        {
            rules.Insert(insertionIndex, new JsonObject
            {
                ["clash_mode"] = "global",
                ["action"] = "route",
                ["outbound"] = global,
            });
        }
        else if (replaceGlobal)
        {
            globalRule["action"] = "route";
            globalRule["outbound"] = global;
        }
    }

    private static string? FindGlobalOutbound(
        IReadOnlyList<JsonObject> outbounds,
        JsonObject? route,
        bool includeManaged)
    {
        var rules = route?["rules"] as JsonArray;
        var existing = rules?.OfType<JsonObject>().FirstOrDefault(static rule => string.Equals(
            rule["clash_mode"]?.ToString(),
            "global",
            StringComparison.OrdinalIgnoreCase))?["outbound"]?.ToString();
        if ((includeManaged || !IsManagedGlobalSelector(existing))
            && FindOutbound(outbounds, existing) is { } existingOutbound
            && IsGlobalOutboundType(existingOutbound["type"]?.ToString()))
        {
            return existing;
        }

        var finalOutbound = route?["final"]?.ToString();
        if (!string.IsNullOrWhiteSpace(finalOutbound))
        {
            var final = FindOutbound(outbounds, finalOutbound);
            if (final is not null && IsGlobalOutboundType(final["type"]?.ToString()))
                return finalOutbound;
        }

        return outbounds.FirstOrDefault(static outbound => string.Equals(
                   outbound["type"]?.ToString(),
                   "selector",
                   StringComparison.OrdinalIgnoreCase)
                   && !IsManagedGlobalSelector(outbound["tag"]?.ToString()))?["tag"]?.ToString()
               ?? outbounds.FirstOrDefault(static outbound => string.Equals(
                   outbound["type"]?.ToString(),
                   "urltest",
                   StringComparison.OrdinalIgnoreCase))?["tag"]?.ToString()
               ?? outbounds.FirstOrDefault(static outbound =>
                   IsGlobalOutboundType(outbound["type"]?.ToString())
                   && !string.IsNullOrWhiteSpace(outbound["tag"]?.ToString()))?["tag"]?.ToString();
    }

    private static IReadOnlyList<string> BuildGlobalSelectorMembers(
        IReadOnlyList<JsonObject> outbounds,
        string source,
        string managedGlobal)
    {
        var byTag = outbounds
            .Where(static outbound => !string.IsNullOrWhiteSpace(outbound["tag"]?.ToString()))
            .ToDictionary(
                static outbound => outbound["tag"]!.ToString(),
                StringComparer.Ordinal);
        var members = new List<string>();
        var added = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)
                || string.Equals(tag, managedGlobal, StringComparison.Ordinal)
                || !visited.Add(tag)
                || !byTag.TryGetValue(tag, out var outbound))
            {
                return;
            }

            var type = outbound["type"]?.ToString()?.ToLowerInvariant();
            if (type is "direct" or "block" or "dns")
                return;
            if (type == "selector")
            {
                if (outbound["outbounds"] is JsonArray selectorMembers)
                {
                    foreach (var member in selectorMembers)
                        Add(member?.ToString());
                }
                return;
            }

            if (added.Add(tag))
                members.Add(tag);
            if (type == "urltest" && outbound["outbounds"] is JsonArray testedMembers)
            {
                foreach (var member in testedMembers)
                    Add(member?.ToString());
            }
        }

        Add(source);
        foreach (var outbound in outbounds)
        {
            if (!string.Equals(outbound["type"]?.ToString(), "selector", StringComparison.OrdinalIgnoreCase))
                Add(outbound["tag"]?.ToString());
        }
        return members;
    }

    private static bool IsGlobalOutboundType(string? type)
    {
        return !string.IsNullOrWhiteSpace(type)
               && type.ToLowerInvariant() is not ("direct" or "block" or "dns");
    }

    private static string? FindDirectOutbound(IReadOnlyList<JsonObject> outbounds)
    {
        return outbounds.FirstOrDefault(static outbound => string.Equals(
            outbound["type"]?.ToString(),
            "direct",
            StringComparison.OrdinalIgnoreCase))?["tag"]?.ToString();
    }

    private static JsonObject? FindOutbound(
        IReadOnlyList<JsonObject> outbounds,
        string? tag)
    {
        return string.IsNullOrWhiteSpace(tag)
            ? null
            : outbounds.FirstOrDefault(outbound => string.Equals(
                outbound["tag"]?.ToString(),
                tag,
                StringComparison.Ordinal));
    }

    private static bool ContainsRoutingMode(JsonArray rules, string expected)
    {
        return rules.OfType<JsonObject>().Any(rule => string.Equals(
            rule["clash_mode"]?.ToString(),
            expected,
            StringComparison.OrdinalIgnoreCase));
    }

    private static int FindInitialRuleIndex(JsonArray rules)
    {
        var index = 0;
        while (index < rules.Count && rules[index] is JsonObject rule
               && rule["action"]?.ToString() is "sniff" or "resolve" or "route-options" or "hijack-dns")
        {
            index++;
        }

        return index;
    }

    private static string NormalizeRoutingMode(string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "global" => "global",
            "direct" => "direct",
            _ => "rule",
        };
    }

    private static void ApplyCustomDns(JsonObject configuration, string? customDnsServer)
    {
        var dns = configuration["dns"] as JsonObject;
        if (dns is null && string.IsNullOrWhiteSpace(customDnsServer))
        {
            return;
        }

        dns ??= EnsureObject(configuration, "dns");
        var servers = EnsureArray(dns, "servers");
        foreach (var existing in servers.OfType<JsonObject>()
                     .Where(static server => server["tag"]?.ToString() is "dns-custom" or "dns-bootstrap")
                     .ToArray())
        {
            servers.Remove(existing);
        }

        var rules = EnsureArray(dns, "rules");
        foreach (var rule in rules.OfType<JsonObject>()
                     .Where(static rule => rule["server"]?.ToString() == "dns-custom")
                     .ToArray())
        {
            rules.Remove(rule);
        }
        if (dns["final"]?.ToString() == "dns-custom")
            dns.Remove("final");
        var route = EnsureObject(configuration, "route");
        if (route["default_domain_resolver"]?.ToString() is "dns-custom" or "dns-bootstrap")
            route.Remove("default_domain_resolver");

        if (string.IsNullOrWhiteSpace(customDnsServer))
            return;

        var value = customDnsServer.Trim();
        var customServers = new List<JsonObject>();
        var defaultDomainResolver = "dns-custom";
        if (string.Equals(value, "local", StringComparison.OrdinalIgnoreCase))
        {
            customServers.Add(new JsonObject
            {
                ["type"] = "local",
                ["tag"] = "dns-custom",
            });
        }
        else
        {
            var server = CreateDnsServer(value, out var needsResolver);
            if (needsResolver)
            {
                customServers.Add(new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = "dns-bootstrap",
                });
                server["domain_resolver"] = "dns-bootstrap";
                defaultDomainResolver = "dns-bootstrap";
            }
            customServers.Add(server);
        }

        for (var index = customServers.Count - 1; index >= 0; index--)
            servers.Insert(0, customServers[index]);

        rules.Insert(0, new JsonObject
        {
            ["action"] = "route",
            ["server"] = "dns-custom",
        });
        route["default_domain_resolver"] = defaultDomainResolver;
    }

    private static JsonObject CreateDnsServer(string value, out bool needsResolver)
    {
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            needsResolver = !System.Net.IPAddress.TryParse(value, out _);
            return new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "dns-custom",
                ["server"] = value,
            };
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme.ToLowerInvariant() is not ("udp" or "tcp" or "tls" or "quic" or "https" or "h3")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException(
                "Custom DNS must be local, an IP/domain, or a udp/tcp/tls/quic/https/h3 URL.");
        }

        var type = uri.Scheme.ToLowerInvariant();
        needsResolver = !System.Net.IPAddress.TryParse(uri.Host, out _);
        var server = new JsonObject
        {
            ["type"] = type,
            ["tag"] = "dns-custom",
            ["server"] = uri.Host,
        };
        if (!uri.IsDefaultPort)
            server["server_port"] = uri.Port;
        if (type is "https" or "h3" && uri.AbsolutePath != "/")
            server["path"] = uri.AbsolutePath;
        return server;
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
