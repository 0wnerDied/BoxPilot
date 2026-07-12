using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Tests;

public sealed class CoreServiceIdentityTests
{
    [Fact]
    public void IdentityIsStableAndScopedToOwnerAndDataRoot()
    {
        var first = CoreServiceIdentity.Create("owner-a", "/Users/test/BoxPilot");

        Assert.Equal(first, CoreServiceIdentity.Create("owner-a", "/Users/test/BoxPilot/"));
        Assert.NotEqual(first, CoreServiceIdentity.Create("owner-b", "/Users/test/BoxPilot"));
        Assert.NotEqual(first, CoreServiceIdentity.Create("owner-a", "/Users/test/Other"));
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void MacLayoutUsesProtectedServiceAndRuntimeLocations()
    {
        const string identity = "0123456789abcdef";

        var layout = CoreServiceLayout.Create(
            CoreServicePlatform.MacOS,
            identity,
            "/Users/test/Library/Application Support/BoxPilot",
            "/Library/Application Support/BoxPilot/Services",
            "/var/run/boxpilot",
            "/Library/LaunchDaemons");

        Assert.Equal("tech.0b1t.boxpilot.core.0123456789abcdef", layout.ServiceName);
        Assert.StartsWith("/Library/Application Support/BoxPilot/Services/", layout.ServiceExecutablePath);
        Assert.Equal("/var/run/boxpilot/0123456789abcdef.sock", layout.Endpoint);
        Assert.Equal(
            "/Library/LaunchDaemons/tech.0b1t.boxpilot.core.0123456789abcdef.plist",
            layout.LaunchDaemonPath);
    }

    [Fact]
    public void WindowsLayoutUsesProgramFilesAndNamedPipe()
    {
        const string identity = "0123456789abcdef";

        var layout = CoreServiceLayout.Create(
            CoreServicePlatform.Windows,
            identity,
            @"C:\Users\test\AppData\Local\BoxPilot",
            @"C:\Program Files\BoxPilot\Services",
            string.Empty,
            string.Empty);

        Assert.Equal("BoxPilotCore_0123456789abcdef", layout.ServiceName);
        Assert.EndsWith("BoxPilot.exe", layout.ServiceExecutablePath, StringComparison.Ordinal);
        Assert.Equal("BoxPilot.Core.0123456789abcdef", layout.Endpoint);
        Assert.Null(layout.LaunchDaemonPath);
    }

    [Fact]
    public void LayoutRejectsPathLikeIdentity()
    {
        Assert.Throws<InvalidDataException>(() => CoreServiceLayout.Create(
            CoreServicePlatform.MacOS,
            "../../service000",
            "/tmp/data",
            "/tmp/service",
            "/tmp/run",
            "/tmp/daemons"));
    }
}
