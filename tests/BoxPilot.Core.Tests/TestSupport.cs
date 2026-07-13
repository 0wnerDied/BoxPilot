using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Tests;

public abstract class SingBoxTestBase : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();

    protected SingBoxTestBase(string? dataDirectory = null)
    {
        TestRoot = directory.Path;
        Paths = new AppPaths(dataDirectory is null
            ? TestRoot
            : Path.Combine(TestRoot, dataDirectory));
        Core = new SingBoxService(Paths);
        Config = new SingBoxConfigService(Paths, Core);
    }

    protected string TestRoot { get; }

    protected AppPaths Paths { get; }

    protected SingBoxService Core { get; }

    protected SingBoxConfigService Config { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Core.DisposeAsync();
        directory.Dispose();
    }
}

internal static class SingBoxTestExtensions
{
    public static async Task<bool> InitializeIfInstalledAsync(this SingBoxService core)
    {
        try
        {
            await core.InitializeAsync();
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}

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
