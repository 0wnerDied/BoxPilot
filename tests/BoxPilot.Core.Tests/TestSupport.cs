namespace BoxPilot.Core.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string prefix = "boxpilot-tests")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : this((request, _) => handler(request))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } requestUri)
            Requests.Add(requestUri);
        return handler(request, cancellationToken);
    }
}
