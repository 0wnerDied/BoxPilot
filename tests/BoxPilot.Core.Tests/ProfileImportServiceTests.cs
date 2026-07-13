using System.Net;
using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Tests;

public sealed class ProfileImportServiceTests : IAsyncLifetime
{
    private const string ClashSubscription = """
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

    private const string SingBoxSubscription = """
        {
          "outbounds": [
            { "type": "socks", "tag": "Edge", "server": "example.test", "server_port": 1080 }
          ]
        }
        """;

    private readonly TemporaryDirectory directory = new();
    private readonly SingBoxService core;
    private readonly SingBoxConfigService config;
    private readonly ProfileRepository repository;
    private readonly CustomRoutingService customRouting;

    public ProfileImportServiceTests()
    {
        var paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        config = new SingBoxConfigService(paths, core);
        repository = new ProfileRepository(paths);
        customRouting = new CustomRoutingService(paths, config);
    }

    [Fact]
    public async Task FetchSubscriptionPrefersClashRepresentationWithSourceGroups()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.EndsWith("/sub/clash", request.RequestUri?.AbsolutePath);
            Assert.Equal("?token=opaque", request.RequestUri?.Query);
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return TextResponse(ClashSubscription, "application/yaml");
        });
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

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
    public async Task FetchSubscriptionIgnoresNativeJsonFromClashCandidate()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri?.AbsolutePath.EndsWith(
            "/clash",
            StringComparison.Ordinal) == true
            ? TextResponse(SingBoxSubscription, "application/json")
            : TextResponse(ClashSubscription, "application/yaml"));
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

        var download = await service.FetchSubscriptionAsync(
            new Uri("https://example.test/sub"),
            new AppSettings());

        Assert.Equal(SubscriptionFormat.ClashYaml, download.Parsed?.Format);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("target=clash", handler.Requests[1].Query);
    }

    [Fact]
    public async Task FetchSubscriptionFallsBackWhenProviderRejectsClashRepresentations()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var requestUri = request.RequestUri!;
            var requestsClash = requestUri.AbsolutePath.EndsWith(
                                    "/clash",
                                    StringComparison.Ordinal)
                                || requestUri.Query.Contains(
                                    "target=clash",
                                    StringComparison.Ordinal);
            return requestsClash
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : TextResponse(SingBoxSubscription, "application/json");
        });
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

        var download = await service.FetchSubscriptionAsync(
            new Uri("https://example.test/sub"),
            new AppSettings());

        Assert.Equal(SubscriptionFormat.SingBoxJson, download.Parsed?.Format);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(handler.Requests[2].Query);
    }

    [Fact]
    public async Task FetchSubscriptionRespectsExplicitFormatPath()
    {
        const string subscription =
            "vless://11111111-1111-1111-1111-111111111111@example.test:443#Edge";
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/sub/v2ray", request.RequestUri?.AbsolutePath);
            Assert.Empty(request.RequestUri!.Query);
            return TextResponse(subscription, "text/plain");
        });
        using var httpClient = new HttpClient(handler);
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

        var download = await service.FetchSubscriptionAsync(
            new Uri("https://example.test/sub/v2ray"),
            new AppSettings());

        Assert.Equal(SubscriptionFormat.UriList, download.Parsed?.Format);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateSubscriptionReappliesPersistentCustomRuleSets()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(ClashSubscription, "application/yaml")));
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);
        var imported = await service.ImportSubscriptionAsync(
            "Provider",
            new Uri("https://example.test/sub/clash"),
            new AppSettings());
        var sourcePath = Path.Combine(directory.Path, "private.json");
        await File.WriteAllTextAsync(sourcePath, """{ "version": 3, "rules": [] }""");
        var configuration = await repository.ReadConfigurationAsync(imported.Profile);
        var change = await customRouting.ImportAsync(
            imported.Profile,
            sourcePath,
            "Source group",
            configuration);
        await repository.WriteConfigurationAsync(imported.Profile, change.Configuration);

        var updated = await service.UpdateSubscriptionAsync(imported.Profile, new AppSettings());
        var updatedConfiguration = await repository.ReadConfigurationAsync(updated.Profile);

        Assert.Contains(change.RuleSet.Tag, updatedConfiguration);
        Assert.Contains("Source group", updatedConfiguration);
    }

    [Fact]
    public async Task ImportWithoutModesPersistsSelectedPolicyAcrossUpdates()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(ClashSubscription, "application/yaml")));
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

        var imported = await service.ImportSubscriptionAsync(
            "Provider",
            new Uri("https://example.test/sub/clash"),
            new AppSettings());
        var initialConfiguration = await repository.ReadConfigurationAsync(imported.Profile);

        Assert.False(config.SupportsStandardRoutingModes(initialConfiguration));
        Assert.True(config.CanAddStandardRoutingModes(initialConfiguration));

        var optedOut = imported.Profile with { ManageStandardRoutingModes = false };
        await repository.UpdateAsync(optedOut);
        var unchanged = await service.UpdateSubscriptionAsync(optedOut, new AppSettings());
        var unchangedConfiguration = await repository.ReadConfigurationAsync(unchanged.Profile);

        Assert.False(config.SupportsStandardRoutingModes(unchangedConfiguration));
        Assert.False(unchanged.Profile.ManageStandardRoutingModes);

        var optedIn = unchanged.Profile with { ManageStandardRoutingModes = true };
        await repository.UpdateAsync(optedIn);
        var updated = await service.UpdateSubscriptionAsync(optedIn, new AppSettings());
        var updatedConfiguration = await repository.ReadConfigurationAsync(updated.Profile);

        Assert.True(config.SupportsStandardRoutingModes(updatedConfiguration));
        var parsed = config.Parse(updatedConfiguration);
        Assert.Equal(
            SingBoxConfigService.ManagedGlobalSelectorTag,
            config.GetGlobalProxyGroup(parsed));
        Assert.True(updated.Profile.ManageStandardRoutingModes);
    }

    [Fact]
    public async Task ImportPreservesProviderModeRules()
    {
        const string subscription = """
            {
              "outbounds": [
                { "type": "socks", "tag": "Edge", "server": "example.test", "server_port": 1080 },
                { "type": "direct", "tag": "direct" }
              ],
              "route": {
                "rules": [
                  { "clash_mode": "direct", "action": "route", "outbound": "direct" },
                  { "clash_mode": "global", "action": "route", "outbound": "Edge" }
                ],
                "final": "Edge"
              }
            }
            """;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(subscription, "application/json")));
        using var subscriptionClient = new SubscriptionClient(httpClient);
        var service = CreateService(subscriptionClient);

        var imported = await service.ImportSubscriptionAsync(
            "Provider",
            new Uri("https://example.test/sub/singbox"),
            new AppSettings());
        var configuration = await repository.ReadConfigurationAsync(imported.Profile);

        Assert.True(config.SupportsStandardRoutingModes(configuration));
        Assert.False(config.CanAddStandardRoutingModes(configuration));
        Assert.Null(imported.Profile.ManageStandardRoutingModes);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await core.DisposeAsync();
        directory.Dispose();
    }

    private ProfileImportService CreateService(SubscriptionClient subscriptionClient)
    {
        return new ProfileImportService(
            subscriptionClient,
            new SubscriptionParser(config),
            config,
            repository,
            customRouting);
    }

    private static HttpResponseMessage TextResponse(string content, string mediaType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType),
        };
    }
}
