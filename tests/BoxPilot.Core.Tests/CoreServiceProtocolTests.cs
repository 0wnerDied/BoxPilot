using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Tests;

public sealed class CoreServiceProtocolTests
{
    [Fact]
    public async Task ProtocolRoundTripsUtf8ConfigurationAndState()
    {
        var expected = new CoreServiceMessage
        {
            Type = "request",
            ProtocolVersion = CoreServiceProtocol.Version,
            RequestId = 42,
            Command = "start",
            Configuration = "{\"tag\":\"日本节点\"}",
            State = CoreState.Starting,
        };
        await using var stream = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);

        await CoreServiceProtocol.WriteAsync(stream, expected, gate, CancellationToken.None);
        stream.Position = 0;
        var actual = await CoreServiceProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ProtocolRejectsTruncatedPayload()
    {
        await using var stream = new MemoryStream([0, 0, 0, 10, (byte)'{']);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => CoreServiceProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void TokenAuthenticationRejectsDifferentAndMalformedHashes()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var hash = CoreServiceToken.Hash(token);

        Assert.True(CoreServiceToken.MatchesHash(token, hash));
        Assert.False(CoreServiceToken.MatchesHash(token + "0", hash));
        Assert.False(CoreServiceToken.MatchesHash(token, "invalid"));
    }

    [Fact]
    public void EntrypointsRecognizeServiceInstallAndRemovalOnly()
    {
        Assert.True(CoreServiceInstaller.IsInvocation([CoreServiceInstaller.ModeArgument]));
        Assert.True(CoreServiceInstaller.IsInvocation([CoreServiceInstaller.UninstallModeArgument]));
        Assert.True(CoreServiceHost.IsInvocation([CoreServiceHost.ModeArgument]));
        Assert.False(CoreServiceInstaller.IsInvocation([CoreServiceHost.ModeArgument]));
        Assert.False(CoreServiceHost.IsInvocation(["--unknown"]));
    }
}
