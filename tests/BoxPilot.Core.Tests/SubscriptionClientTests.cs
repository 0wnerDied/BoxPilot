using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public sealed class SubscriptionClientTests
{
    [Fact]
    public async Task FetchIgnoresLegacyCharsetAndDecodesUtf8()
    {
        const string payload = "{\"remarks\":\"中文节点 🚀\"}";
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "iso-8859-1",
            };
            return response;
        }));
        using var client = new SubscriptionClient(httpClient);

        var result = await client.FetchAsync(new Uri("https://example.test/sub"), "BoxPilot tests");

        Assert.Equal(payload, result.Content);
    }

    [Fact]
    public async Task FetchRejectsInvalidUtf8()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x7b, 0xff, 0x7d]),
            }));
        using var client = new SubscriptionClient(httpClient);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => client.FetchAsync(new Uri("https://example.test/sub"), "BoxPilot tests"));
    }
}
