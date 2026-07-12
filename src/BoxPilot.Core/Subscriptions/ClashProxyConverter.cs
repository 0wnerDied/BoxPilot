using System.Text.Json.Nodes;

namespace BoxPilot.Core.Subscriptions;

internal static class ClashProxyConverter
{
    public static JsonObject? Convert(JsonObject source, ICollection<string> warnings)
    {
        var type = JsonValueReader.String(source, "type")?.Trim().ToLowerInvariant();
        var name = JsonValueReader.String(source, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
        {
            warnings.Add("Skipped a proxy without a type or name.");
            return null;
        }

        if (type is "direct")
            return new JsonObject { ["type"] = "direct", ["tag"] = name };
        if (type is "reject" or "block")
            return new JsonObject { ["type"] = "block", ["tag"] = name };

        var server = JsonValueReader.String(source, "server");
        var port = JsonValueReader.Integer(source, "port");
        if (string.IsNullOrWhiteSpace(server) || port is null)
        {
            warnings.Add($"Skipped '{name}': server or port is missing.");
            return null;
        }

        var outbound = new JsonObject
        {
            ["type"] = NormalizeType(type),
            ["tag"] = name,
            ["server"] = server,
            ["server_port"] = port.Value,
        };

        switch (type)
        {
            case "vless":
                AddRequired(outbound, "uuid", JsonValueReader.String(source, "uuid"), name, warnings);
                JsonValueReader.AddIfNotEmpty(outbound, "flow", JsonValueReader.String(source, "flow"));
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "packet_encoding",
                    JsonValueReader.String(source, "packet-encoding", "packet_encoding"));
                break;
            case "vmess":
                AddRequired(outbound, "uuid", JsonValueReader.String(source, "uuid"), name, warnings);
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "security",
                    JsonValueReader.String(source, "cipher", "security") ?? "auto");
                JsonValueReader.AddIfPresent(
                    outbound,
                    "alter_id",
                    JsonValueReader.Integer(source, "alterId", "alter-id", "alter_id"));
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "packet_encoding",
                    JsonValueReader.String(source, "packet-encoding", "packet_encoding"));
                break;
            case "trojan":
            case "anytls":
            case "naive":
                AddRequired(outbound, "password", JsonValueReader.String(source, "password"), name, warnings);
                if (type == "naive")
                    JsonValueReader.AddIfNotEmpty(outbound, "username", JsonValueReader.String(source, "username"));
                break;
            case "ss":
            case "shadowsocks":
                outbound["type"] = "shadowsocks";
                AddRequired(outbound, "method", JsonValueReader.String(source, "cipher", "method"), name, warnings);
                AddRequired(outbound, "password", JsonValueReader.String(source, "password"), name, warnings);
                AddShadowsocksPlugin(source, outbound);
                break;
            case "socks":
            case "socks5":
                outbound["type"] = "socks";
                outbound["version"] = "5";
                JsonValueReader.AddIfNotEmpty(outbound, "username", JsonValueReader.String(source, "username"));
                JsonValueReader.AddIfNotEmpty(outbound, "password", JsonValueReader.String(source, "password"));
                break;
            case "http":
            case "https":
                outbound["type"] = "http";
                JsonValueReader.AddIfNotEmpty(outbound, "username", JsonValueReader.String(source, "username"));
                JsonValueReader.AddIfNotEmpty(outbound, "password", JsonValueReader.String(source, "password"));
                break;
            case "hysteria2":
            case "hy2":
                outbound["type"] = "hysteria2";
                AddRequired(
                    outbound,
                    "password",
                    JsonValueReader.String(source, "password", "auth", "auth-str"),
                    name,
                    warnings);
                AddBandwidth(source, outbound);
                AddHysteria2Obfuscation(source, outbound);
                break;
            case "hysteria":
                AddRequired(
                    outbound,
                    "auth_str",
                    JsonValueReader.String(source, "auth-str", "auth_str", "auth"),
                    name,
                    warnings);
                AddBandwidth(source, outbound);
                JsonValueReader.AddIfNotEmpty(outbound, "obfs", JsonValueReader.String(source, "obfs"));
                JsonValueReader.AddIfNotEmpty(outbound, "protocol", JsonValueReader.String(source, "protocol"));
                break;
            case "tuic":
                AddRequired(outbound, "uuid", JsonValueReader.String(source, "uuid"), name, warnings);
                AddRequired(outbound, "password", JsonValueReader.String(source, "password"), name, warnings);
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "congestion_control",
                    JsonValueReader.String(source, "congestion-controller", "congestion_control"));
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "udp_relay_mode",
                    JsonValueReader.String(source, "udp-relay-mode", "udp_relay_mode"));
                outbound["zero_rtt_handshake"] = JsonValueReader.Boolean(
                    source,
                    false,
                    "reduce-rtt",
                    "zero_rtt_handshake");
                break;
            case "snell":
                AddRequired(outbound, "password", JsonValueReader.String(source, "psk", "password"), name, warnings);
                JsonValueReader.AddIfPresent(outbound, "version", JsonValueReader.Integer(source, "version"));
                break;
            case "ssh":
                JsonValueReader.AddIfNotEmpty(outbound, "user", JsonValueReader.String(source, "username", "user"));
                JsonValueReader.AddIfNotEmpty(outbound, "password", JsonValueReader.String(source, "password"));
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "private_key_path",
                    JsonValueReader.String(source, "private-key", "private_key_path"));
                break;
            case "wireguard":
                AddRequired(
                    outbound,
                    "private_key",
                    JsonValueReader.String(source, "private-key", "private_key"),
                    name,
                    warnings);
                var addresses = JsonValueReader.Array(source, "ip", "address")
                    ?? (JsonValueReader.String(source, "ip", "address") is { } address
                        ? new JsonArray(address)
                        : null);
                if (addresses is not null)
                    outbound["local_address"] = addresses.DeepClone();
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "peer_public_key",
                    JsonValueReader.String(source, "public-key", "peer_public_key"));
                JsonValueReader.AddIfNotEmpty(
                    outbound,
                    "pre_shared_key",
                    JsonValueReader.String(source, "pre-shared-key", "pre_shared_key"));
                JsonValueReader.AddIfPresent(outbound, "mtu", JsonValueReader.Integer(source, "mtu"));
                break;
            case "shadowtls":
                AddRequired(outbound, "password", JsonValueReader.String(source, "password"), name, warnings);
                JsonValueReader.AddIfPresent(outbound, "version", JsonValueReader.Integer(source, "version"));
                break;
            default:
                warnings.Add($"Skipped '{name}': Clash proxy type '{type}' is not supported by sing-box.");
                return null;
        }

        AddTls(source, outbound, server, type == "https");
        AddTransport(source, outbound);
        AddMultiplex(source, outbound);
        AddDialFields(source, outbound);
        return outbound;
    }

    private static string NormalizeType(string type)
    {
        return type switch
        {
            "ss" => "shadowsocks",
            "socks5" => "socks",
            "https" => "http",
            "hy2" => "hysteria2",
            _ => type,
        };
    }

    private static void AddRequired(
        JsonObject target,
        string field,
        string? value,
        string name,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
            warnings.Add($"'{name}' does not define required field '{field}'.");
        else
            target[field] = value;
    }

    private static void AddTls(JsonObject source, JsonObject outbound, string server, bool forceEnabled)
    {
        var reality = JsonValueReader.Object(source, "reality-opts", "reality_opts");
        var enabled = forceEnabled
                      || JsonValueReader.Boolean(source, false, "tls")
                      || reality is not null;
        if (!enabled)
            return;

        var tls = new JsonObject { ["enabled"] = true };
        var serverName = JsonValueReader.String(source, "servername", "sni", "server-name");
        if (string.IsNullOrWhiteSpace(serverName) && !System.Net.IPAddress.TryParse(server, out _))
            serverName = server;
        JsonValueReader.AddIfNotEmpty(tls, "server_name", serverName);
        tls["insecure"] = JsonValueReader.Boolean(source, false, "skip-cert-verify", "insecure");

        var alpn = source["alpn"];
        if (alpn is not null)
            tls["alpn"] = JsonValueReader.StringArray(alpn);

        var fingerprint = JsonValueReader.String(source, "client-fingerprint", "fingerprint");
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = fingerprint,
            };
        }

        if (reality is not null)
        {
            var realityTarget = new JsonObject { ["enabled"] = true };
            JsonValueReader.AddIfNotEmpty(
                realityTarget,
                "public_key",
                JsonValueReader.String(reality, "public-key", "public_key"));
            JsonValueReader.AddIfNotEmpty(
                realityTarget,
                "short_id",
                JsonValueReader.String(reality, "short-id", "short_id"));
            tls["reality"] = realityTarget;
        }

        outbound["tls"] = tls;
    }

    private static void AddTransport(JsonObject source, JsonObject outbound)
    {
        var network = JsonValueReader.String(source, "network")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(network) || network is "tcp")
            return;

        JsonObject transport;
        switch (network)
        {
            case "ws":
            case "websocket":
                var webSocket = JsonValueReader.Object(source, "ws-opts", "ws_opts") ?? source;
                transport = new JsonObject { ["type"] = "ws" };
                JsonValueReader.AddIfNotEmpty(transport, "path", JsonValueReader.String(webSocket, "path"));
                CopyObject(webSocket, transport, "headers", "headers");
                JsonValueReader.AddIfPresent(
                    transport,
                    "max_early_data",
                    JsonValueReader.Integer(webSocket, "max-early-data", "max_early_data"));
                JsonValueReader.AddIfNotEmpty(
                    transport,
                    "early_data_header_name",
                    JsonValueReader.String(webSocket, "early-data-header-name", "early_data_header_name"));
                break;
            case "grpc":
                var grpc = JsonValueReader.Object(source, "grpc-opts", "grpc_opts") ?? source;
                transport = new JsonObject { ["type"] = "grpc" };
                JsonValueReader.AddIfNotEmpty(
                    transport,
                    "service_name",
                    JsonValueReader.String(grpc, "grpc-service-name", "service-name", "service_name"));
                break;
            case "h2":
            case "http":
                var http = JsonValueReader.Object(source, "h2-opts", "http-opts", "http_opts") ?? source;
                transport = new JsonObject { ["type"] = "http" };
                JsonValueReader.AddIfNotEmpty(transport, "path", JsonValueReader.String(http, "path"));
                var host = http["host"];
                if (host is not null)
                    transport["host"] = JsonValueReader.StringArray(host);
                CopyObject(http, transport, "headers", "headers");
                break;
            case "httpupgrade":
            case "http-upgrade":
                var upgrade = JsonValueReader.Object(source, "http-upgrade-opts", "httpupgrade-opts") ?? source;
                transport = new JsonObject { ["type"] = "httpupgrade" };
                JsonValueReader.AddIfNotEmpty(transport, "host", JsonValueReader.String(upgrade, "host"));
                JsonValueReader.AddIfNotEmpty(transport, "path", JsonValueReader.String(upgrade, "path"));
                CopyObject(upgrade, transport, "headers", "headers");
                break;
            case "quic":
                transport = new JsonObject { ["type"] = "quic" };
                break;
            default:
                return;
        }

        outbound["transport"] = transport;
    }

    private static void AddMultiplex(JsonObject source, JsonObject outbound)
    {
        var multiplexSource = JsonValueReader.Object(source, "smux", "multiplex");
        if (multiplexSource is null)
            return;

        var enabled = JsonValueReader.Boolean(multiplexSource, false, "enabled");
        var multiplex = new JsonObject { ["enabled"] = enabled };
        JsonValueReader.AddIfNotEmpty(
            multiplex,
            "protocol",
            JsonValueReader.String(multiplexSource, "protocol"));
        JsonValueReader.AddIfPresent(
            multiplex,
            "max_connections",
            JsonValueReader.Integer(multiplexSource, "max-connections", "max_connections"));
        JsonValueReader.AddIfPresent(
            multiplex,
            "min_streams",
            JsonValueReader.Integer(multiplexSource, "min-streams", "min_streams"));
        JsonValueReader.AddIfPresent(
            multiplex,
            "max_streams",
            JsonValueReader.Integer(multiplexSource, "max-streams", "max_streams"));
        outbound["multiplex"] = multiplex;
    }

    private static void AddDialFields(JsonObject source, JsonObject outbound)
    {
        JsonValueReader.AddIfNotEmpty(
            outbound,
            "bind_interface",
            JsonValueReader.String(source, "interface-name", "bind_interface"));
        JsonValueReader.AddIfPresent(
            outbound,
            "routing_mark",
            JsonValueReader.Integer(source, "routing-mark", "routing_mark"));

        if (JsonValueReader.Boolean(source, false, "tfo", "tcp-fast-open"))
            outbound["tcp_fast_open"] = true;
        if (JsonValueReader.Boolean(source, false, "mptcp"))
            outbound["tcp_multi_path"] = true;
    }

    private static void AddBandwidth(JsonObject source, JsonObject outbound)
    {
        JsonValueReader.AddIfPresent(
            outbound,
            "up_mbps",
            JsonValueReader.Integer(source, "up", "up-mbps", "up_mbps"));
        JsonValueReader.AddIfPresent(
            outbound,
            "down_mbps",
            JsonValueReader.Integer(source, "down", "down-mbps", "down_mbps"));
    }

    private static void AddHysteria2Obfuscation(JsonObject source, JsonObject outbound)
    {
        var type = JsonValueReader.String(source, "obfs");
        var password = JsonValueReader.String(source, "obfs-password", "obfs_password");
        if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(password))
            return;

        outbound["obfs"] = new JsonObject
        {
            ["type"] = string.IsNullOrWhiteSpace(type) ? "salamander" : type,
            ["password"] = password,
        };
    }

    private static void AddShadowsocksPlugin(JsonObject source, JsonObject outbound)
    {
        var plugin = JsonValueReader.String(source, "plugin");
        if (string.IsNullOrWhiteSpace(plugin))
            return;

        outbound["plugin"] = plugin;
        if (source["plugin-opts"] is JsonObject pluginOptions)
        {
            var values = pluginOptions
                .Select(static pair => $"{pair.Key}={pair.Value}")
                .ToArray();
            outbound["plugin_opts"] = string.Join(';', values);
        }
        else
        {
            JsonValueReader.AddIfNotEmpty(
                outbound,
                "plugin_opts",
                JsonValueReader.String(source, "plugin-opts", "plugin_opts"));
        }
    }

    private static void CopyObject(
        JsonObject source,
        JsonObject target,
        string sourceName,
        string targetName)
    {
        if (source[sourceName] is JsonObject value)
            target[targetName] = value.DeepClone();
    }
}
