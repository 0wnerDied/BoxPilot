using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class ClashApiClientTests
{
    [Fact]
    public async Task GetProxyChoicesReturnsSubscriptionPolicyGroupsInSourceOrder()
    {
        const string responseJson = """
            {
              "proxies": {
                "GLOBAL": {
                  "type": "Fallback",
                  "now": "Proxy",
                  "all": ["Proxy"]
                },
                "Proxy": {
                  "type": "Selector",
                  "now": "日本 A01",
                  "all": ["日本 A01", "Nested"]
                },
                "Nested": {
                  "type": "Selector",
                  "now": "direct",
                  "all": ["direct"]
                },
                "Auto": {
                  "type": "URLTest",
                  "now": "日本 A01",
                  "all": ["日本 A01"]
                },
                "日本 A01": {
                  "type": "VLESS",
                  "udp": true,
                  "history": [{ "delay": 193 }]
                },
                "direct": {
                  "type": "Direct",
                  "udp": true,
                  "history": []
                }
              }
            }
            """;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            }));
        using var client = new ClashApiClient(9090, string.Empty, httpClient);

        var groups = await client.GetProxyChoicesAsync();

        Assert.Equal(["Proxy", "Nested", "Auto"], groups.Select(static group => group.Group));
        Assert.True(groups[0].IsSelectable);
        Assert.False(groups[2].IsSelectable);
        var node = groups[0].Options[0];
        Assert.Equal("日本 A01", node.Name);
        Assert.Equal("VLESS", node.Type);
        Assert.Equal(193, node.Delay);
        Assert.True(node.SupportsUdp);
        Assert.False(node.IsGroup);
        Assert.True(groups[0].Options[1].IsGroup);
    }

    [Fact]
    public async Task SelectProxyEscapesUnicodeGroupAndWritesJsonName()
    {
        Uri? requestUri = null;
        string? requestBody = null;
        string? authorization = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestUri = request.RequestUri;
            requestBody = await request.Content!.ReadAsStringAsync();
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var client = new ClashApiClient(9090, "test-secret", httpClient);

        await client.SelectProxyAsync("🔰 手动选择", "日本 A01");

        Assert.Equal("/proxies/🔰 手动选择", Uri.UnescapeDataString(requestUri!.AbsolutePath));
        Assert.Equal("日本 A01", JsonNode.Parse(requestBody!)?["name"]?.ToString());
        Assert.DoesNotContain("\\u", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer test-secret", authorization);
    }
}
