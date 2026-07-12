using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public ClashApiClient(int port, string secret, HttpClient? httpClient = null)
    {
        if (port is <= 0 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port));

        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        ownsClient = httpClient is null;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        Secret = secret ?? string.Empty;
    }

    public Uri BaseAddress { get; }

    public string Secret { get; }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "version");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await ReadObjectAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return json?["version"]?.ToString() ?? "unknown";
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
            .Where(static pair => pair.Value is JsonObject value && value["all"] is JsonArray)
            .Select(pair =>
            {
                var value = pair.Value!.AsObject();
                return new ProxyChoice(
                    pair.Key,
                    value["now"]?.ToString() ?? string.Empty,
                    value["all"]!.AsArray()
                        .Select(static item => item?.ToString() ?? string.Empty)
                        .Where(static item => item.Length > 0)
                        .ToArray());
            })
            .OrderBy(static item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            new JsonObject { ["name"] = proxy }.ToJsonString(),
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
        if (!string.IsNullOrWhiteSpace(Secret))
            socket.Options.SetRequestHeader("Authorization", $"Bearer {Secret}");

        var builder = new UriBuilder(BaseAddress)
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
            var json = JsonNode.Parse(message) as JsonObject;
            if (json is null)
                continue;
            progress.Report(new TrafficSnapshot(
                json["up"]?.GetValue<long>() ?? 0,
                json["down"]?.GetValue<long>() ?? 0));
        }
    }

    public void Dispose()
    {
        if (ownsClient)
            httpClient.Dispose();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        if (!string.IsNullOrWhiteSpace(Secret))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Secret);
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
}
