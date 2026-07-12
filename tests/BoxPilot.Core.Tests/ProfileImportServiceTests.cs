using System.Net;
using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Tests;

public sealed class ProfileImportServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boxpilot-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task FetchSubscriptionPrefersClashRepresentationWithSourceGroups()
    {
        const string yaml = """
                            proxies:
                              - name: Edge
                                type: socks5
                                server: example.test
                                port: 1080
                            proxy-groups:
                              - name: Source group
                                type: select
                                proxies: [Edge, DIRECT]
                            rules:
                              - MATCH,Source group
                            """;
        var handler = new RequestHandler(request =>
        {
            Assert.Contains("target=clash", request.RequestUri?.Query);
            Assert.Contains("format=clash", request.RequestUri?.Query);
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return TextResponse(yaml, "application/yaml");
        });
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        await using var core = new SingBoxService(new AppPaths(root));
        var service = CreateService(subscriptionClient, core);

        var download = await service.FetchSubscriptionAsync(
            new Uri("https://example.test/sub?token=opaque"),
            new AppSettings(),
            "\"native-etag\"",
            DateTimeOffset.UnixEpoch,
            nameof(SubscriptionFormat.SingBoxJson));

        Assert.Equal(SubscriptionFormat.ClashYaml, download.Parsed?.Format);
        Assert.Equal(1, download.Parsed?.SourcePolicyGroupCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchSubscriptionFallsBackWhenProviderRejectsClashTarget()
    {
        const string json = """
                            {
                              "outbounds": [
                                { "type": "socks", "tag": "Edge", "server": "example.test", "server_port": 1080 }
                              ]
                            }
                            """;
        var handler = new RequestHandler(request => request.RequestUri?.Query.Contains(
            "target=clash",
            StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : TextResponse(json, "application/json"));
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        await using var core = new SingBoxService(new AppPaths(root));
        var service = CreateService(subscriptionClient, core);

        var download = await service.FetchSubscriptionAsync(
            new Uri("https://example.test/sub"),
            new AppSettings());

        Assert.Equal(SubscriptionFormat.SingBoxJson, download.Parsed?.Format);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(handler.Requests[1].Query);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private ProfileImportService CreateService(
        SubscriptionClient subscriptionClient,
        SingBoxService core)
    {
        var paths = new AppPaths(root);
        var config = new SingBoxConfigService(paths, core);
        return new ProfileImportService(
            subscriptionClient,
            new SubscriptionParser(config),
            config,
            new ProfileRepository(paths));
    }

    private static HttpResponseMessage TextResponse(string content, string mediaType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType),
        };
    }

    private sealed class RequestHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }
}
