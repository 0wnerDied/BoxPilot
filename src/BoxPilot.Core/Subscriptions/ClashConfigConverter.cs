using System.Globalization;
using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Subscriptions;

internal sealed class ClashConfigConverter(SingBoxConfigService configService)
{
    public SubscriptionImportResult Convert(JsonObject clash, SubscriptionBuildOptions options)
    {
        var warnings = new List<string>();
        if (clash["proxy-providers"] is JsonObject providers && providers.Count > 0)
        {
            warnings.Add(
                "Clash proxy-providers are not fetched recursively; import their URLs as separate subscriptions.");
        }

        var proxies = JsonValueReader.Array(clash, "proxies") ?? [];
        var allocator = new SingBoxConfigurationBuilder.TagAllocator();
        allocator.Allocate("direct");
        allocator.Allocate("block");

        var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DIRECT"] = "direct",
            ["REJECT"] = "block",
            ["REJECT-DROP"] = "block",
            ["PASS"] = "direct",
        };
        var outbounds = new JsonArray();
        var nodeTags = new List<string>();

        foreach (var proxy in proxies.OfType<JsonObject>())
        {
            var converted = ClashProxyConverter.Convert(proxy, warnings);
            if (converted is null)
                continue;

            var originalTag = JsonValueReader.String(converted, "tag") ?? "Proxy";
            var tag = allocator.Allocate(originalTag);
            if (!tagMap.TryAdd(originalTag, tag))
                warnings.Add($"Duplicate Clash proxy name '{originalTag}' was renamed to '{tag}'.");
            converted["tag"] = tag;
            JsonNodes.Append(outbounds, converted);
            nodeTags.Add(tag);
        }

        if (nodeTags.Count == 0)
            throw new InvalidDataException("The Clash subscription did not contain supported proxy nodes.");

        var groupSources = JsonValueReader.Array(clash, "proxy-groups")?.OfType<JsonObject>().ToArray()
                           ?? [];
        var groupTags = new Dictionary<JsonObject, string>();
        foreach (var group in groupSources)
        {
            var name = JsonValueReader.String(group, "name") ?? "Proxy";
            var tag = allocator.Allocate(name);
            groupTags[group] = tag;
            tagMap[name] = tag;
        }

        foreach (var group in groupSources)
        {
            var converted = ConvertGroup(
                group,
                groupTags[group],
                tagMap,
                nodeTags,
                warnings);
            if (converted is not null)
                JsonNodes.Append(outbounds, converted);
        }

        string defaultOutbound;
        if (groupSources.Length > 0)
        {
            defaultOutbound = groupTags[groupSources[0]];
        }
        else
        {
            var automaticTag = allocator.Allocate("Auto");
            JsonNodes.Append(outbounds, new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = automaticTag,
                ["outbounds"] = new JsonArray(
                    nodeTags.Select(static tag => JsonValue.Create(tag)).ToArray()),
                ["url"] = "https://www.gstatic.com/generate_204",
                ["interval"] = "5m",
                ["tolerance"] = 50,
                ["interrupt_exist_connections"] = true,
            });
            defaultOutbound = allocator.Allocate("Proxy");
            JsonNodes.Append(outbounds, new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = defaultOutbound,
                ["outbounds"] = new JsonArray(new[] { automaticTag }
                    .Concat(nodeTags)
                    .Select(static tag => JsonValue.Create(tag))
                    .ToArray()),
                ["default"] = automaticTag,
                ["interrupt_exist_connections"] = true,
            });
        }

        JsonNodes.Append(outbounds, new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        JsonNodes.Append(outbounds, new JsonObject { ["type"] = "block", ["tag"] = "block" });

        var configuration = SingBoxConfigurationBuilder.CreateBaseConfiguration(outbounds, defaultOutbound);
        var route = configuration["route"]!.AsObject();
        var routeRules = route["rules"]!.AsArray();
        var ruleSets = new JsonArray();
        var finalOutbound = ConvertRules(
            JsonValueReader.Array(clash, "rules"),
            tagMap,
            routeRules,
            ruleSets,
            defaultOutbound,
            warnings);
        if (ruleSets.Count > 0)
            route["rule_set"] = ruleSets;
        route["final"] = finalOutbound;

        configuration = configService.ApplyRuntimeOptions(
            configuration,
            SingBoxConfigurationBuilder.ToSettings(options));
        return new SubscriptionImportResult(
            SubscriptionFormat.ClashYaml,
            configuration,
            nodeTags.Count,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            groupSources.Length);
    }

    private static JsonObject? ConvertGroup(
        JsonObject source,
        string tag,
        IReadOnlyDictionary<string, string> tagMap,
        IReadOnlyList<string> nodeTags,
        ICollection<string> warnings)
    {
        var type = JsonValueReader.String(source, "type")?.ToLowerInvariant() ?? "select";
        var members = JsonValueReader.Array(source, "proxies")?
            .Select(item => item?.ToString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(item => MapTarget(item!, tagMap))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            ?? [];

        if (members.Length == 0)
            members = nodeTags.ToArray();

        if (type is "url-test" or "fallback" or "load-balance")
        {
            if (type is "fallback" or "load-balance")
            {
                warnings.Add(
                    $"Clash group '{tag}' uses '{type}'; mapped to sing-box URLTest semantics.");
            }

            var urlTest = new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = tag,
                ["outbounds"] = new JsonArray(members.Select(static member => JsonValue.Create(member)).ToArray()),
                ["url"] = JsonValueReader.String(source, "url")
                          ?? "https://www.gstatic.com/generate_204",
                ["interval"] = NormalizeInterval(JsonValueReader.String(source, "interval")),
                ["tolerance"] = JsonValueReader.Integer(source, "tolerance") ?? 50,
                ["interrupt_exist_connections"] = true,
            };
            return urlTest;
        }

        if (type is "relay")
        {
            warnings.Add($"Clash relay group '{tag}' was mapped to a manual selector.");
        }
        else if (type is not "select")
        {
            warnings.Add($"Unknown Clash group type '{type}' in '{tag}' was mapped to a selector.");
        }

        var configuredDefault = JsonValueReader.String(source, "default");
        var mappedDefault = string.IsNullOrWhiteSpace(configuredDefault)
            ? null
            : MapTarget(configuredDefault, tagMap);
        var selectedDefault = mappedDefault is not null
                              && members.Contains(mappedDefault, StringComparer.Ordinal)
            ? mappedDefault
            : members[0];

        return new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = tag,
            ["outbounds"] = new JsonArray(members.Select(static member => JsonValue.Create(member)).ToArray()),
            ["default"] = selectedDefault,
            ["interrupt_exist_connections"] = true,
        };
    }

    private static string ConvertRules(
        JsonArray? sourceRules,
        IReadOnlyDictionary<string, string> tagMap,
        JsonArray destination,
        JsonArray destinationRuleSets,
        string defaultOutbound,
        ICollection<string> warnings)
    {
        if (sourceRules is null)
            return defaultOutbound;

        var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ruleSetTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var finalOutbound = defaultOutbound;

        foreach (var ruleNode in sourceRules)
        {
            var ruleText = ruleNode?.ToString().Trim();
            if (string.IsNullOrWhiteSpace(ruleText) || ruleText.StartsWith('#'))
                continue;

            var fields = ruleText.Split(',', StringSplitOptions.TrimEntries);
            var type = fields[0].ToUpperInvariant();
            if (type is "MATCH" or "FINAL")
            {
                if (fields.Length > 1)
                    finalOutbound = MapTarget(fields[1], tagMap) ?? defaultOutbound;
                continue;
            }

            if (fields.Length < 3)
            {
                Increment(unsupported, type);
                continue;
            }

            var target = MapTarget(fields[2], tagMap);
            if (string.IsNullOrWhiteSpace(target))
            {
                Increment(unsupported, type);
                continue;
            }

            var rule = new JsonObject
            {
                ["action"] = "route",
                ["outbound"] = target,
            };
            var value = fields[1];

            switch (type)
            {
                case "DOMAIN":
                    rule["domain"] = new JsonArray(value);
                    break;
                case "DOMAIN-SUFFIX":
                    rule["domain_suffix"] = new JsonArray(value.TrimStart('.'));
                    break;
                case "DOMAIN-KEYWORD":
                    rule["domain_keyword"] = new JsonArray(value);
                    break;
                case "DOMAIN-REGEX":
                    rule["domain_regex"] = new JsonArray(value);
                    break;
                case "IP-CIDR":
                case "IP-CIDR6":
                    rule["ip_cidr"] = new JsonArray(value);
                    break;
                case "SRC-IP-CIDR":
                    rule["source_ip_cidr"] = new JsonArray(value);
                    break;
                case "GEOIP" when IsPrivateGeoIp(value):
                    rule["ip_is_private"] = true;
                    break;
                case "GEOIP":
                    rule["rule_set"] = new JsonArray(EnsureGeoRuleSet(
                        "geoip",
                        value,
                        destinationRuleSets,
                        ruleSetTags));
                    break;
                case "GEOSITE":
                    rule["rule_set"] = new JsonArray(EnsureGeoRuleSet(
                        "geosite",
                        value,
                        destinationRuleSets,
                        ruleSetTags));
                    break;
                case "DST-PORT":
                    AddPort(rule, value, "port", "port_range");
                    break;
                case "SRC-PORT":
                    AddPort(rule, value, "source_port", "source_port_range");
                    break;
                case "PROCESS-NAME":
                    rule["process_name"] = new JsonArray(value);
                    break;
                case "PROCESS-PATH":
                    rule["process_path"] = new JsonArray(value);
                    break;
                case "NETWORK":
                    rule["network"] = value.ToLowerInvariant();
                    break;
                case "IN-TYPE":
                    rule["inbound"] = new JsonArray(value);
                    break;
                default:
                    Increment(unsupported, type);
                    continue;
            }

            JsonNodes.Append(destination, rule);
        }

        foreach (var item in unsupported.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            warnings.Add($"Ignored {item.Value} Clash '{item.Key}' rule(s) without a direct sing-box mapping.");
        }

        return finalOutbound;
    }

    private static string EnsureGeoRuleSet(
        string database,
        string value,
        JsonArray destination,
        IDictionary<string, string> tags)
    {
        var name = value.Trim().ToLowerInvariant();
        var key = $"{database}:{name}";
        if (tags.TryGetValue(key, out var existingTag))
            return existingTag;

        var tag = $"{database}-{name}";
        tags[key] = tag;
        JsonNodes.Append(destination, new JsonObject
        {
            ["type"] = "remote",
            ["tag"] = tag,
            ["format"] = "binary",
            ["url"] = "https://fastly.jsdelivr.net/gh/SagerNet/"
                      + $"sing-{database}@rule-set/{Uri.EscapeDataString(tag)}.srs",
            ["download_detour"] = "direct",
            ["update_interval"] = "1d",
        });
        return tag;
    }

    private static bool IsPrivateGeoIp(string value)
    {
        return value.Trim().Equals("LAN", StringComparison.OrdinalIgnoreCase)
               || value.Trim().Equals("PRIVATE", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPort(JsonObject rule, string value, string portName, string rangeName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            rule[portName] = new JsonArray(port);
        else
            rule[rangeName] = new JsonArray(value.Replace('-', ':'));
    }

    private static string? MapTarget(string value, IReadOnlyDictionary<string, string> tagMap)
    {
        return tagMap.TryGetValue(value.Trim(), out var mapped) ? mapped : null;
    }

    private static string NormalizeInterval(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "3m";
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return $"{Math.Max(seconds, 10)}s";
        return value;
    }

    private static void Increment(IDictionary<string, int> values, string key)
    {
        values[key] = values.TryGetValue(key, out var count) ? count + 1 : 1;
    }
}
