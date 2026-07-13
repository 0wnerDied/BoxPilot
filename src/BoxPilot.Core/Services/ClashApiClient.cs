using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly Uri baseAddress;
    private readonly string secret;

    public ClashApiClient(int port, string secret, HttpClient? httpClient = null)
    {
        if (port is <= 0 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port));

        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        ownsClient = httpClient is null;
        baseAddress = new Uri($"http://127.0.0.1:{port}/");
        this.secret = secret ?? string.Empty;
    }

    public async Task<IReadOnlyList<ProxyChoice>> GetProxyChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "proxies");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await ReadObjectAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (json?["proxies"] is not JsonObject proxies)
            return [];

        return proxies
            .Where(static pair => pair.Value is JsonObject value
                                  && value["all"] is JsonArray
                                  && IsPolicyGroup(value["type"]?.ToString()))
            .Select(pair =>
            {
                var value = pair.Value!.AsObject();
                var type = value["type"]?.ToString() ?? string.Empty;
                return new ProxyChoice(
                    pair.Key,
                    string.Equals(type, "Selector", StringComparison.OrdinalIgnoreCase),
                    value["now"]?.ToString() ?? string.Empty,
                    value["all"]!.AsArray()
                        .Select(item => CreateProxyNode(item?.ToString(), proxies))
                        .Where(static item => item is not null)
                        .Cast<ProxyNode>()
                        .ToArray());
            })
            .ToArray();
    }

    private static bool IsPolicyGroup(string? type)
    {
        return string.Equals(type, "Selector", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "URLTest", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SelectProxyAsync(
        string group,
        string proxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);

        using var request = CreateRequest(
            HttpMethod.Put,
            $"proxies/{Uri.EscapeDataString(group)}");
        request.Content = new StringContent(
            new JsonObject { ["name"] = proxy }.ToJsonString(JsonDefaults.SerializerOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        var normalized = mode?.ToLowerInvariant() switch
        {
            "global" => "global",
            "direct" => "direct",
            _ => "rule",
        };
        using var request = CreateRequest(HttpMethod.Patch, "configs");
        request.Content = new StringContent(
            new JsonObject { ["mode"] = normalized }.ToJsonString(JsonDefaults.SerializerOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int?> TestDelayAsync(
        string proxy,
        string testUrl = "https://www.gstatic.com/generate_204",
        int timeoutMilliseconds = 5_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);
        var path = $"proxies/{Uri.EscapeDataString(proxy)}/delay"
                   + $"?timeout={timeoutMilliseconds}&url={Uri.EscapeDataString(testUrl)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await ReadObjectAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return json?["delay"]?.GetValue<int>();
    }

    public async Task StreamTrafficAsync(
        IProgress<TrafficSnapshot> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(secret))
            socket.Options.SetRequestHeader("Authorization", $"Bearer {secret}");

        var builder = new UriBuilder(baseAddress)
        {
            Scheme = "ws",
            Path = "traffic",
        };
        await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[4 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;
                await message.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken)
                    .ConfigureAwait(false);
            }
            while (!received.EndOfMessage);

            message.Position = 0;
            var json = await JsonNode.ParseAsync(message, cancellationToken: cancellationToken)
                .ConfigureAwait(false) as JsonObject;
            if (json is null)
                continue;
            progress.Report(new TrafficSnapshot(
                json["up"]?.GetValue<long>() ?? 0,
                json["down"]?.GetValue<long>() ?? 0));
        }
    }

    public async Task<TrafficTotals> GetTrafficTotalsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "connections");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await ReadObjectAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return new TrafficTotals(
            ReadNonNegativeInt64(json, "uploadTotal"),
            ReadNonNegativeInt64(json, "downloadTotal"));
    }

    public void Dispose()
    {
        if (ownsClient)
            httpClient.Dispose();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(baseAddress, path));
        if (!string.IsNullOrWhiteSpace(secret))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        return request;
    }

    private static async Task<JsonObject?> ReadObjectAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            as JsonObject;
    }

    private static ProxyNode? CreateProxyNode(string? name, JsonObject proxies)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var value = proxies[name] as JsonObject;
        return new ProxyNode(
            name,
            value?["type"]?.ToString() ?? "Unknown",
            ReadLatestDelay(value),
            value?["udp"]?.GetValue<bool>() ?? false,
            value?["all"] is JsonArray);
    }

    private static int? ReadLatestDelay(JsonObject? proxy)
    {
        if (proxy?["history"] is not JsonArray history
            || history.LastOrDefault() is not JsonObject latest
            || latest["delay"] is not JsonValue delay
            || !delay.TryGetValue<int>(out var value))
        {
            return null;
        }

        return value;
    }

    private static long ReadNonNegativeInt64(JsonObject? value, string propertyName)
    {
        return value?[propertyName] is JsonValue property
               && property.TryGetValue<long>(out var result)
            ? Math.Max(0, result)
            : 0;
    }
}
