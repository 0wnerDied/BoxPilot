using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class CustomRoutingService(
    AppPaths paths,
    SingBoxConfigService configService)
{
    private const long MaximumRuleSetSize = 64 * 1024 * 1024;
    private const string ManagedTagPrefix = "boxpilot-custom-";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<CustomRuleSet>> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return index.RuleSets.Where(ruleSet => ruleSet.ProfileId == profileId).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<RoutingOutbound> GetRoutingOutbounds(string configuration)
    {
        var parsed = configService.Parse(configuration);
        if (parsed["outbounds"] is not JsonArray outbounds)
            return [];

        return outbounds
            .OfType<JsonObject>()
            .Where(static outbound => IsRoutingTarget(
                outbound["type"]?.ToString() ?? string.Empty))
            .Select(static outbound => new RoutingOutbound(
                outbound["tag"]?.ToString() ?? string.Empty))
            .Where(static outbound => !string.IsNullOrWhiteSpace(outbound.Tag))
            .DistinctBy(static outbound => outbound.Tag, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> ApplyAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return configService.Serialize(Apply(
                configService.Parse(configuration),
                index.RuleSets.Where(ruleSet => ruleSet.ProfileId == profileId)));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CustomRuleSetChange> ImportAsync(
        Profile profile,
        string sourcePath,
        string outbound,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outbound);

        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists)
            throw new FileNotFoundException("The rule-set file does not exist.", source.FullName);
        if (source.Length > MaximumRuleSetSize)
            throw new InvalidDataException("The rule-set file exceeds the 64 MiB import limit.");

        var format = source.Extension.ToLowerInvariant() switch
        {
            ".json" => RuleSetFormat.Source,
            ".srs" => RuleSetFormat.Binary,
            _ => throw new InvalidDataException("Only sing-box JSON and SRS rule-sets are supported."),
        };
        if (format == RuleSetFormat.Source)
            await ValidateSourceRuleSetAsync(source.FullName, cancellationToken).ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? destinationPath = null;
        try
        {
            var parsed = configService.Parse(configuration);
            EnsureOutboundExists(parsed, outbound);

            var id = Guid.NewGuid();
            var fileName = $"{id:N}{source.Extension.ToLowerInvariant()}";
            var ruleSet = new CustomRuleSet
            {
                Id = id,
                ProfileId = profile.Id,
                Name = Path.GetFileNameWithoutExtension(source.Name),
                FileName = fileName,
                Outbound = outbound,
                Format = format,
                Source = RuleSetSource.Local,
            };

            var directory = paths.GetRuleSetDirectory(profile.Id);
            Directory.CreateDirectory(directory);
            destinationPath = Path.Combine(directory, fileName);
            await CopyAtomicallyAsync(source.FullName, destinationPath, cancellationToken)
                .ConfigureAwait(false);

            return await AddRuleSetAsync(parsed, ruleSet, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (destinationPath is not null && File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CustomRuleSetChange> AddRemoteAsync(
        Profile profile,
        string url,
        string outbound,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outbound);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("A remote rule-set requires an HTTP or HTTPS URL.");
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var format = extension == ".srs" ? RuleSetFormat.Binary : RuleSetFormat.Source;
        var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(name))
            name = uri.Host;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = configService.Parse(configuration);
            EnsureOutboundExists(parsed, outbound);
            var ruleSet = new CustomRuleSet
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                Name = name,
                Url = uri.AbsoluteUri,
                Outbound = outbound,
                Format = format,
                Source = RuleSetSource.Remote,
            };
            return await AddRuleSetAsync(parsed, ruleSet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> RemoveAsync(
        Profile profile,
        Guid ruleSetId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var ruleSet = index.RuleSets.FirstOrDefault(item => item.Id == ruleSetId
                                                                && item.ProfileId == profile.Id);
            if (ruleSet is null)
                return configuration;

            index.RuleSets.Remove(ruleSet);
            var updated = configService.Serialize(Apply(
                configService.Parse(configuration),
                index.RuleSets.Where(item => item.ProfileId == profile.Id)));
            await configService.ValidateOrThrowAsync(updated, null, cancellationToken)
                .ConfigureAwait(false);
            await SaveAsync(index, cancellationToken).ConfigureAwait(false);

            if (ruleSet.Source == RuleSetSource.Local)
            {
                var path = GetRuleSetPath(ruleSet);
                if (File.Exists(path))
                    File.Delete(path);
            }
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (index.RuleSets.RemoveAll(item => item.ProfileId == profileId) > 0)
                await SaveAsync(index, cancellationToken).ConfigureAwait(false);

            var directory = paths.GetRuleSetDirectory(profileId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        finally
        {
            gate.Release();
        }
    }

    internal JsonObject Apply(
        JsonObject configuration,
        IEnumerable<CustomRuleSet> profileRuleSets)
    {
        var activeRuleSets = profileRuleSets.ToArray();
        var clone = configuration.DeepClone().AsObject();
        if (clone["route"] is JsonObject existingRoute)
        {
            RemoveManagedRules(
                existingRoute["rules"] as JsonArray,
                existingRoute["rule_set"] as JsonArray);
        }
        if (activeRuleSets.Length == 0)
            return clone;

        var route = JsonNodes.EnsureObject(clone, "route");
        var rules = JsonNodes.EnsureArray(route, "rules");
        var definitions = JsonNodes.EnsureArray(route, "rule_set");
        var targets = GetOutboundTags(clone);
        var insertionIndex = FindRuleInsertionIndex(rules);
        foreach (var ruleSet in activeRuleSets)
        {
            if (!targets.Contains(ruleSet.Outbound))
            {
                throw new InvalidDataException(
                    $"Custom rule-set '{ruleSet.Name}' targets missing outbound '{ruleSet.Outbound}'.");
            }

            var definition = new JsonObject
            {
                ["type"] = ruleSet.Source == RuleSetSource.Remote ? "remote" : "local",
                ["tag"] = ruleSet.Tag,
                ["format"] = ruleSet.Format == RuleSetFormat.Source ? "source" : "binary",
            };
            if (ruleSet.Source == RuleSetSource.Remote)
            {
                if (!Uri.TryCreate(ruleSet.Url, UriKind.Absolute, out var remoteUri))
                    throw new InvalidDataException($"Custom rule-set '{ruleSet.Name}' has an invalid URL.");
                definition["url"] = remoteUri.AbsoluteUri;
                definition["update_interval"] = ruleSet.UpdateInterval;
                var direct = FindDirectOutbound(configuration);
                if (direct is not null)
                    definition["download_detour"] = direct;
            }
            else
            {
                var path = GetRuleSetPath(ruleSet);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Custom rule-set '{ruleSet.Name}' is missing.", path);
                definition["path"] = path;
            }
            JsonNodes.Append(definitions, definition);
            rules.Insert(insertionIndex++, new JsonObject
            {
                ["rule_set"] = new JsonArray(ruleSet.Tag),
                ["action"] = "route",
                ["outbound"] = ruleSet.Outbound,
            });
        }

        return clone;
    }

    private static void RemoveManagedRules(JsonArray? rules, JsonArray? definitions)
    {
        if (definitions is not null)
        {
            foreach (var definition in definitions.OfType<JsonObject>()
                         .Where(static definition => IsManagedTag(definition["tag"]?.ToString()))
                         .ToArray())
            {
                definitions.Remove(definition);
            }
        }

        if (rules is not null)
        {
            foreach (var rule in rules.OfType<JsonObject>()
                         .Where(static rule => rule["rule_set"] is JsonArray tags
                                               && tags.Any(tag => IsManagedTag(tag?.ToString())))
                         .ToArray())
            {
                rules.Remove(rule);
            }
        }
    }

    private static int FindRuleInsertionIndex(JsonArray rules)
    {
        var index = 0;
        while (index < rules.Count && rules[index] is JsonObject rule)
        {
            if (rule["clash_mode"] is not null)
            {
                index++;
                continue;
            }
            var action = rule["action"]?.ToString();
            if (action is not ("sniff" or "resolve" or "route-options" or "hijack-dns"))
                break;
            index++;
        }

        return index;
    }

    private static HashSet<string> GetOutboundTags(JsonObject configuration)
    {
        return configuration["outbounds"] is JsonArray outbounds
            ? outbounds.OfType<JsonObject>()
                .Select(static outbound => outbound["tag"]?.ToString())
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal)
            : [];
    }

    private static void EnsureOutboundExists(JsonObject configuration, string outbound)
    {
        if (!GetOutboundTags(configuration).Contains(outbound))
            throw new InvalidDataException($"Outbound '{outbound}' does not exist in this profile.");
    }

    private string GetRuleSetPath(CustomRuleSet ruleSet)
    {
        if (string.IsNullOrWhiteSpace(ruleSet.FileName))
            throw new InvalidDataException("The custom local rule-set has no file name.");
        var fileName = Path.GetFileName(ruleSet.FileName);
        if (!string.Equals(fileName, ruleSet.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("The custom rule-set file name is invalid.");
        return Path.Combine(paths.GetRuleSetDirectory(ruleSet.ProfileId), fileName);
    }

    private static string? FindDirectOutbound(JsonObject configuration)
    {
        return configuration["outbounds"] is JsonArray outbounds
            ? outbounds.OfType<JsonObject>().FirstOrDefault(static outbound => string.Equals(
                outbound["type"]?.ToString(),
                "direct",
                StringComparison.OrdinalIgnoreCase))?["tag"]?.ToString()
            : null;
    }

    private async Task<CustomRoutingIndex> LoadAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.CustomRoutingFile))
            return new CustomRoutingIndex();

        try
        {
            await using var stream = File.OpenRead(paths.CustomRoutingFile);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    JsonDefaults.Context.CustomRoutingIndex,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new CustomRoutingIndex();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The BoxPilot custom routing index is invalid JSON.", exception);
        }
    }

    private Task SaveAsync(CustomRoutingIndex index, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(index, JsonDefaults.Context.CustomRoutingIndex);
        return AtomicFile.WriteAllTextAsync(paths.CustomRoutingFile, json, cancellationToken);
    }

    private async Task<CustomRuleSetChange> AddRuleSetAsync(
        JsonObject configuration,
        CustomRuleSet ruleSet,
        CancellationToken cancellationToken)
    {
        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        index.RuleSets.Add(ruleSet);
        var updated = configService.Serialize(Apply(
            configuration,
            index.RuleSets.Where(item => item.ProfileId == ruleSet.ProfileId)));
        await configService.ValidateOrThrowAsync(updated, null, cancellationToken)
            .ConfigureAwait(false);
        await SaveAsync(index, cancellationToken).ConfigureAwait(false);
        return new CustomRuleSetChange(ruleSet, updated);
    }

    private static async Task ValidateSourceRuleSetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException("A source rule-set must be a JSON object.");
            if (root["version"] is not JsonValue || root["rules"] is not JsonArray)
                throw new InvalidDataException("A source rule-set requires version and rules fields.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The source rule-set is not valid JSON.", exception);
        }
    }

    private static async Task CopyAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool IsRoutingTarget(string type)
    {
        return type is "selector" or "urltest" or "direct" or "block";
    }

    private static bool IsManagedTag(string? tag)
    {
        return tag?.StartsWith(ManagedTagPrefix, StringComparison.Ordinal) == true;
    }
}
