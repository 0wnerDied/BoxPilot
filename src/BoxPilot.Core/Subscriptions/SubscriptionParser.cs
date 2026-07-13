using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using YamlDotNet.Core;

namespace BoxPilot.Core.Subscriptions;

public sealed class SubscriptionParser
{
    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly SingBoxConfigService configService;
    private readonly SingBoxConfigurationBuilder configurationBuilder;
    private readonly ClashConfigConverter clashConverter;

    public SubscriptionParser(SingBoxConfigService configService)
    {
        this.configService = configService;
        configurationBuilder = new SingBoxConfigurationBuilder(configService);
        clashConverter = new ClashConfigConverter(configService);
    }

    internal SubscriptionImportResult Parse(string content, SubscriptionBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(options);

        var trimmed = content.Trim().TrimStart('\uFEFF');
        if (TryParseJson(trimmed, options, out var jsonResult))
            return jsonResult;
        if (LooksLikeClash(trimmed) && TryParseClash(trimmed, options, out var clashResult))
            return clashResult;

        if (TryDecodeBase64(trimmed, out var decoded))
        {
            var result = ParseUriList(decoded, options, SubscriptionFormat.Base64UriList);
            if (result is not null)
                return result;
        }

        return ParseUriList(trimmed, options, SubscriptionFormat.UriList)
               ?? throw new InvalidDataException(
                   "Unsupported subscription. Use sing-box JSON, Clash YAML, or a URI list.");
    }

    private bool TryParseJson(
        string content,
        SubscriptionBuildOptions options,
        out SubscriptionImportResult result)
    {
        result = null!;
        if (!content.StartsWith('{') && !content.StartsWith('['))
            return false;

        try
        {
            var node = JsonNode.Parse(content, documentOptions: JsonDocumentOptions);
            JsonObject configuration;
            int nodeCount;
            if (node is JsonObject root && root["outbounds"] is JsonArray outbounds)
            {
                configuration = configService.ApplyRuntimeOptions(
                    root,
                    SingBoxConfigurationBuilder.ToSettings(options));
                nodeCount = outbounds.OfType<JsonObject>().Count(IsProxyOutbound);
            }
            else if (node is JsonArray outboundArray)
            {
                var proxyOutbounds = outboundArray.OfType<JsonObject>().ToArray();
                var warnings = new List<string>();
                configuration = configurationBuilder.Build(proxyOutbounds, options, warnings);
                result = new SubscriptionImportResult(
                    SubscriptionFormat.SingBoxJson,
                    configuration,
                    proxyOutbounds.Length,
                    warnings);
                return true;
            }
            else
            {
                throw new InvalidDataException("JSON subscription has no sing-box outbounds array.");
            }

            result = new SubscriptionImportResult(
                SubscriptionFormat.SingBoxJson,
                configuration,
                nodeCount,
                []);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryParseClash(
        string content,
        SubscriptionBuildOptions options,
        out SubscriptionImportResult result)
    {
        try
        {
            var clash = YamlJsonConverter.ParseObject(content);
            if (clash["proxies"] is not JsonArray)
            {
                result = null!;
                return false;
            }

            result = clashConverter.Convert(clash, options);
            return true;
        }
        catch (YamlException)
        {
            result = null!;
            return false;
        }
    }

    private SubscriptionImportResult? ParseUriList(
        string content,
        SubscriptionBuildOptions options,
        SubscriptionFormat format)
    {
        var warnings = new List<string>();
        var outbounds = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ProxyUriParser.Parse(line, warnings))
            .Where(static outbound => outbound is not null)
            .Cast<JsonObject>()
            .ToArray();
        if (outbounds.Length == 0)
            return null;

        var configuration = configurationBuilder.Build(outbounds, options, warnings);
        return new SubscriptionImportResult(format, configuration, outbounds.Length, warnings);
    }

    private static bool LooksLikeClash(string content)
    {
        return content.Contains("proxies:", StringComparison.Ordinal)
               || content.Contains("proxy-groups:", StringComparison.Ordinal);
    }

    private static bool TryDecodeBase64(string content, out string decoded)
    {
        decoded = string.Empty;
        if (content.Any(char.IsWhiteSpace) && content.Contains("://", StringComparison.Ordinal))
            return false;

        try
        {
            decoded = ProxyUriParser.DecodeBase64(content);
            return decoded.Contains("://", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsProxyOutbound(JsonObject outbound)
    {
        var type = JsonValueReader.String(outbound, "type");
        return type is not ("direct" or "block" or "dns" or "selector" or "urltest");
    }
}
