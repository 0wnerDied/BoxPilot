using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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

    public async Task<SubscriptionFetchResult> FetchAsync(
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
                response.Content.Headers.ContentType?.MediaType,
                response.Headers.ETag?.ToString() ?? etag,
                response.Content.Headers.LastModified ?? lastModified,
                ParsePositiveIntegerHeader(response, "profile-update-interval"),
                ParseQuota(response),
                true);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumSubscriptionBytes)
            throw new InvalidDataException("The subscription exceeds the 16 MiB safety limit.");

        var content = await ReadLimitedStringAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        return new SubscriptionFetchResult(
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            ParsePositiveIntegerHeader(response, "profile-update-interval"),
            ParseQuota(response));
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
        using var reader = new StreamReader(buffer, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int? ParsePositiveIntegerHeader(HttpResponseMessage response, string name)
    {
        if (!TryGetHeader(response, name, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return null;
        }

        return value;
    }

    private static SubscriptionQuota? ParseQuota(HttpResponseMessage response)
    {
        if (!TryGetHeader(response, "subscription-userinfo", out var value))
            return null;

        var fields = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(static part => part.Length == 2)
            .ToDictionary(static part => part[0], static part => part[1], StringComparer.OrdinalIgnoreCase);

        return new SubscriptionQuota(
            ParseLong(fields, "upload"),
            ParseLong(fields, "download"),
            ParseLong(fields, "total"),
            ParseExpiration(fields));
    }

    private static long? ParseLong(IReadOnlyDictionary<string, string> fields, string name)
    {
        return fields.TryGetValue(name, out var value)
               && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ParseExpiration(IReadOnlyDictionary<string, string> fields)
    {
        var seconds = ParseLong(fields, "expire");
        if (seconds is null or <= 0)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string value)
    {
        if (response.Headers.TryGetValues(name, out var values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            value = values.FirstOrDefault() ?? string.Empty;
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }
}
