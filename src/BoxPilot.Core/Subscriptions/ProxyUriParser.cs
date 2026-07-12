using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoxPilot.Core.Subscriptions;

internal static class ProxyUriParser
{
    public static JsonObject? Parse(string value, ICollection<string> warnings)
    {
        var line = value.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            return null;

        var separator = line.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            warnings.Add("Skipped a subscription entry without a URI scheme.");
            return null;
        }

        var scheme = line[..separator].ToLowerInvariant();
        try
        {
            return scheme switch
            {
                "vmess" => ParseVmess(line, warnings),
                "ss" => ParseShadowsocks(line, warnings),
                "vless" or "trojan" or "hysteria2" or "hy2" or "tuic"
                    or "socks" or "socks5" or "http" or "https" or "anytls"
                    => ParseStandard(line, scheme, warnings),
                _ => Unsupported(scheme, warnings),
            };
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException or JsonException)
        {
            warnings.Add($"Skipped an invalid {scheme} URI: {exception.Message}");
            return null;
        }
    }

    private static JsonObject? ParseStandard(
        string value,
        string scheme,
        ICollection<string> warnings)
    {
        var uri = new Uri(value);
        var query = ParseQuery(uri.Query);
        var name = DecodeFragment(uri.Fragment, scheme);
        var source = new JsonObject
        {
            ["name"] = name,
            ["type"] = scheme,
            ["server"] = uri.Host,
            ["port"] = uri.Port,
        };

        var credentials = uri.UserInfo.Split(':', 2);
        var firstCredential = credentials.Length > 0 ? Uri.UnescapeDataString(credentials[0]) : string.Empty;
        var secondCredential = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty;

        switch (scheme)
        {
            case "vless":
                source["uuid"] = firstCredential;
                break;
            case "trojan":
            case "hysteria2":
            case "hy2":
            case "anytls":
                source["password"] = firstCredential;
                break;
            case "tuic":
                source["uuid"] = firstCredential;
                source["password"] = secondCredential;
                break;
            case "socks":
            case "socks5":
            case "http":
            case "https":
                source["username"] = firstCredential;
                source["password"] = secondCredential;
                break;
        }

        CopyQuery(
            query,
            source,
            "security",
            "tls",
            static value => JsonValue.Create(value is "tls" or "reality"));
        CopyQuery(query, source, "sni", "servername");
        CopyQuery(query, source, "type", "network");
        CopyQuery(query, source, "flow", "flow");
        CopyQuery(query, source, "fp", "client-fingerprint");
        CopyQuery(
            query,
            source,
            "allowInsecure",
            "skip-cert-verify",
            static value => JsonValue.Create(ParseBoolean(value)));
        CopyQuery(
            query,
            source,
            "insecure",
            "skip-cert-verify",
            static value => JsonValue.Create(ParseBoolean(value)));
        CopyQuery(query, source, "congestion_control", "congestion-controller");
        CopyQuery(query, source, "udp_relay_mode", "udp-relay-mode");
        CopyQuery(query, source, "alpn", "alpn", SplitCommaSeparated);
        CopyQuery(query, source, "obfs", "obfs");
        CopyQuery(query, source, "obfs-password", "obfs-password");
        CopyQuery(
            query,
            source,
            "upmbps",
            "up",
            static value => JsonValue.Create(ParseInteger(value)));
        CopyQuery(
            query,
            source,
            "downmbps",
            "down",
            static value => JsonValue.Create(ParseInteger(value)));

        if (query.TryGetValue("pbk", out var publicKey) || query.TryGetValue("publicKey", out publicKey))
        {
            source["reality-opts"] = new JsonObject
            {
                ["public-key"] = publicKey,
                ["short-id"] = query.GetValueOrDefault("sid") ?? query.GetValueOrDefault("shortId"),
            };
            source["tls"] = true;
        }

        AddTransportOptions(query, source);
        return ClashProxyConverter.Convert(source, warnings);
    }

    private static JsonObject? ParseVmess(string value, ICollection<string> warnings)
    {
        var encoded = value["vmess://".Length..].Split('#', 2)[0];
        var json = DecodeBase64(encoded);
        var sourceJson = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("VMess payload is not a JSON object.");

        var name = JsonValueReader.String(sourceJson, "ps", "name") ?? "VMess";
        var source = new JsonObject
        {
            ["name"] = name,
            ["type"] = "vmess",
            ["server"] = JsonValueReader.String(sourceJson, "add", "server"),
            ["port"] = JsonValueReader.Integer(sourceJson, "port"),
            ["uuid"] = JsonValueReader.String(sourceJson, "id", "uuid"),
            ["alterId"] = JsonValueReader.Integer(sourceJson, "aid", "alterId"),
            ["cipher"] = JsonValueReader.String(sourceJson, "scy", "security") ?? "auto",
            ["network"] = JsonValueReader.String(sourceJson, "net", "network"),
            ["servername"] = JsonValueReader.String(sourceJson, "sni", "servername"),
            ["client-fingerprint"] = JsonValueReader.String(sourceJson, "fp"),
            ["tls"] = string.Equals(
                JsonValueReader.String(sourceJson, "tls"),
                "tls",
                StringComparison.OrdinalIgnoreCase),
        };

        var network = JsonValueReader.String(source, "network")?.ToLowerInvariant();
        if (network == "ws")
        {
            source["ws-opts"] = new JsonObject
            {
                ["path"] = JsonValueReader.String(sourceJson, "path"),
                ["headers"] = new JsonObject
                {
                    ["Host"] = JsonValueReader.String(sourceJson, "host"),
                },
            };
        }
        else if (network == "grpc")
        {
            source["grpc-opts"] = new JsonObject
            {
                ["grpc-service-name"] = JsonValueReader.String(sourceJson, "path"),
            };
        }

        return ClashProxyConverter.Convert(source, warnings);
    }

    private static JsonObject? ParseShadowsocks(string value, ICollection<string> warnings)
    {
        var withoutFragment = value.Split('#', 2)[0];
        var fragment = value.Contains('#') ? value[(value.IndexOf('#') + 1)..] : string.Empty;
        var payload = withoutFragment["ss://".Length..];

        if (!payload.Contains('@'))
        {
            payload = DecodeBase64(payload);
        }

        var at = payload.LastIndexOf('@');
        if (at <= 0)
            throw new FormatException("Shadowsocks URI has no server address.");

        var userInfo = payload[..at];
        if (!userInfo.Contains(':'))
            userInfo = DecodeBase64(userInfo);

        var serverPart = payload[(at + 1)..];
        var queryIndex = serverPart.IndexOf('?');
        var query = queryIndex >= 0 ? ParseQuery(serverPart[queryIndex..]) : [];
        if (queryIndex >= 0)
            serverPart = serverPart[..queryIndex];

        var serverUri = new Uri("tcp://" + serverPart);
        var credentials = userInfo.Split(':', 2);
        if (credentials.Length != 2)
            throw new FormatException("Shadowsocks URI credentials are invalid.");

        var source = new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(fragment)
                ? "Shadowsocks"
                : Uri.UnescapeDataString(fragment),
            ["type"] = "ss",
            ["server"] = serverUri.Host,
            ["port"] = serverUri.Port,
            ["cipher"] = Uri.UnescapeDataString(credentials[0]),
            ["password"] = Uri.UnescapeDataString(credentials[1]),
        };

        if (query.TryGetValue("plugin", out var pluginValue))
        {
            var pieces = pluginValue.Split(';', 2);
            source["plugin"] = pieces[0];
            if (pieces.Length == 2)
                source["plugin-opts"] = pieces[1];
        }

        return ClashProxyConverter.Convert(source, warnings);
    }

    private static void AddTransportOptions(
        IReadOnlyDictionary<string, string> query,
        JsonObject source)
    {
        var network = JsonValueReader.String(source, "network")?.ToLowerInvariant();
        if (network == "ws")
        {
            source["ws-opts"] = new JsonObject
            {
                ["path"] = query.GetValueOrDefault("path"),
                ["headers"] = new JsonObject { ["Host"] = query.GetValueOrDefault("host") },
            };
        }
        else if (network == "grpc")
        {
            source["grpc-opts"] = new JsonObject
            {
                ["grpc-service-name"] = query.GetValueOrDefault("serviceName")
                                         ?? query.GetValueOrDefault("service_name"),
            };
        }
        else if (network is "http" or "h2")
        {
            source["h2-opts"] = new JsonObject
            {
                ["path"] = query.GetValueOrDefault("path"),
                ["host"] = SplitCommaSeparated(query.GetValueOrDefault("host") ?? string.Empty),
            };
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var values = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(values[0].Replace('+', ' '));
            var itemValue = values.Length == 2
                ? Uri.UnescapeDataString(values[1].Replace('+', ' '))
                : string.Empty;
            result[key] = itemValue;
        }

        return result;
    }

    private static string DecodeFragment(string fragment, string fallback)
    {
        return string.IsNullOrWhiteSpace(fragment)
            ? char.ToUpperInvariant(fallback[0]) + fallback[1..]
            : Uri.UnescapeDataString(fragment.TrimStart('#'));
    }

    internal static string DecodeBase64(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private static void CopyQuery(
        IReadOnlyDictionary<string, string> query,
        JsonObject target,
        string sourceName,
        string targetName)
    {
        if (query.TryGetValue(sourceName, out var value) && value.Length > 0)
            target[targetName] = value;
    }

    private static void CopyQuery(
        IReadOnlyDictionary<string, string> query,
        JsonObject target,
        string sourceName,
        string targetName,
        Func<string, JsonNode?> converter)
    {
        if (query.TryGetValue(sourceName, out var value) && value.Length > 0)
            target[targetName] = converter(value);
    }

    private static bool ParseBoolean(string value)
    {
        return value is "1" || bool.TryParse(value, out var parsed) && parsed;
    }

    private static int ParseInteger(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static JsonArray SplitCommaSeparated(string value)
    {
        return new JsonArray(value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => JsonValue.Create(item))
            .ToArray());
    }

    private static JsonObject? Unsupported(string scheme, ICollection<string> warnings)
    {
        warnings.Add($"Skipped unsupported subscription URI scheme '{scheme}'.");
        return null;
    }
}
