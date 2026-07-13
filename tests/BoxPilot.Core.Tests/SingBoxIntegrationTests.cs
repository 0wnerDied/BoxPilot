using System.Net;
using System.Net.Sockets;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Tests;

public sealed class SingBoxIntegrationTests : IAsyncLifetime
{
    private readonly TemporaryDirectory directory = new();
    private SingBoxService? core;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledCoreAcceptsStarterConfiguration(bool enableTun)
    {
        var paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        if (!await core.InitializeIfInstalledAsync())
            return;

        var configService = new SingBoxConfigService(paths, core);
        var settings = new AppSettings
        {
            EnableTun = enableTun,
            EnableSystemProxy = false,
            ClashApiPort = 19_091,
            ClashApiSecret = "integration-test",
            AllowLan = true,
            CustomDnsServer = "https://1.1.1.1/dns-query",
        };
        var configuration = configService.Serialize(configService.CreateStarterConfiguration(settings));

        var result = await configService.ValidateAsync(configuration);

        Assert.True(result.IsSuccess, result.CombinedOutput);
    }

    [Fact]
    public async Task InstalledCoreAcceptsManagedClashSelection()
    {
        var paths = new AppPaths(directory.Path);
        core = new SingBoxService(paths);
        if (!await core.InitializeIfInstalledAsync())
            return;

        const string clash = """
            proxies:
              - name: Local probe
                type: socks5
                server: 127.0.0.1
                port: 1080
            proxy-groups:
              - name: Proxy
                type: select
                proxies: [Local probe]
            rules:
              - MATCH,Proxy
            """;
        var configService = new SingBoxConfigService(paths, core);
        var parser = new SubscriptionParser(configService);
        var parsed = parser.Parse(clash, new SubscriptionBuildOptions
        {
            MixedPort = 20_80,
            ClashApiPort = 19_090,
            ClashApiSecret = "integration-test",
            EnableSystemProxy = false,
        });
        var prepared = configService.PrepareManagedSubscription(
            parsed.Configuration,
            "integration-test");

        var result = await configService.ValidateAsync(configService.Serialize(prepared));

        Assert.True(result.IsSuccess, result.CombinedOutput);
    }

    [Fact]
    public async Task SwitchingFromTunServiceToLocalCoreDropsServiceLease()
    {
        var paths = new AppPaths(directory.Path);
        var serviceClient = new FakeCoreServiceClient();
        core = new SingBoxService(paths, () => serviceClient, static () => false);
        if (!await core.InitializeIfInstalledAsync())
            return;

        var (mixedPort, apiPort) = ReserveTcpPorts();
        var configService = new SingBoxConfigService(paths, core);
        var tunPath = Path.Combine(directory.Path, "tun.json");
        var localPath = Path.Combine(directory.Path, "local.json");
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            tunPath,
            configService.Serialize(configService.CreateStarterConfiguration(new AppSettings
            {
                EnableTun = true,
                EnableSystemProxy = false,
                MixedPort = mixedPort,
                ClashApiPort = apiPort,
            })));
        await File.WriteAllTextAsync(
            localPath,
            configService.Serialize(configService.CreateStarterConfiguration(new AppSettings
            {
                EnableTun = false,
                EnableSystemProxy = false,
                MixedPort = mixedPort,
                ClashApiPort = apiPort,
            })));

        await core.StartAsync(tunPath, directory.Path);
        Assert.Equal(directory.Path, serviceClient.WorkingDirectory);
        await core.StopAsync();
        serviceClient.RaiseDisconnected();
        Assert.Equal(CoreState.Stopped, core.State);

        await core.StartAsync(localPath, null);
        await Task.Delay(200);
        serviceClient.RaiseDisconnected();

        Assert.True(serviceClient.Disposed);
        Assert.Equal(CoreState.Running, core.State);
        Assert.NotNull(core.ProcessId);
        await core.StopAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (core is not null)
            await core.DisposeAsync();
        directory.Dispose();
    }

    private static (int First, int Second) ReserveTcpPorts()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
    }

    private sealed class FakeCoreServiceClient : ICoreServiceClient
    {
        public event Action<CoreLogEntry>? LogReceived
        {
            add { }
            remove { }
        }

        public event Action<CoreStateChangedEventArgs>? StateChanged;

        public event Action<string>? Disconnected;

        public bool Disposed { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task StartAsync(
            string executablePath,
            string configurationPath,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            StateChanged?.Invoke(new CoreStateChangedEventArgs(
                CoreState.Running,
                42,
                null));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StateChanged?.Invoke(new CoreStateChangedEventArgs(
                CoreState.Stopped,
                null,
                null));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void RaiseDisconnected()
        {
            Disconnected?.Invoke(CoreServiceErrorCodes.Disconnected);
        }
    }
}
