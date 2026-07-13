using System.Net;
using System.Net.Http.Headers;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class SubscriptionClient : IDisposable
{
    private const int MaximumSubscriptionBytes = 16 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public SubscriptionClient(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            this.httpClient = httpClient;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
        };
        this.httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        ownsClient = true;
    }

    internal async Task<SubscriptionFetchResult> FetchAsync(
        Uri url,
        string userAgent,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("https" or "http"))
            throw new ArgumentException("Subscription URLs must use HTTP or HTTPS.", nameof(url));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? "BoxPilot/0.1 sing-box" : userAgent.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));

        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
            request.Headers.IfNoneMatch.Add(entityTag);
        if (lastModified is not null)
            request.Headers.IfModifiedSince = lastModified;

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new SubscriptionFetchResult(
                string.Empty,
                response.Headers.ETag?.ToString() ?? etag,
                response.Content.Headers.LastModified ?? lastModified,
                true);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumSubscriptionBytes)
            throw new InvalidDataException("The subscription exceeds the 16 MiB safety limit.");

        var content = await ReadLimitedStringAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        return new SubscriptionFetchResult(
            content,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified);
    }

    public void Dispose()
    {
        if (ownsClient)
            httpClient.Dispose();
    }

    private static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];

        while (true)
        {
            var count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > MaximumSubscriptionBytes)
                throw new InvalidDataException("The subscription exceeds the 16 MiB safety limit.");

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return await Utf8Text.ReadToEndAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
