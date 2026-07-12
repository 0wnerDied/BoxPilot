using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Tests;

public sealed class TunConfigurationTests
{
    [Fact]
    public void ContainsTunInboundFindsAnActualTunInbound()
    {
        const string configuration = """
            {
              "inbounds": [
                { "type": "mixed", "tag": "mixed-in" },
                { "type": "TuN", "tag": "tun-in" }
              ]
            }
            """;

        Assert.True(TunConfiguration.ContainsTunInbound(configuration));
    }

    [Fact]
    public void ContainsTunInboundIgnoresTunTextOutsideInboundType()
    {
        const string configuration = """
            {
              "inbounds": [{ "type": "mixed", "tag": "tun" }],
              "outbounds": [{ "type": "direct", "tag": "tun" }]
            }
            """;

        Assert.False(TunConfiguration.ContainsTunInbound(configuration));
    }

    [Fact]
    public void ContainsTunInboundRejectsNonObjectConfiguration()
    {
        Assert.Throws<InvalidDataException>(() => TunConfiguration.ContainsTunInbound("[]"));
    }
}
