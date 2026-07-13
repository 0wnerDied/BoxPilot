using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using BoxPilot.Core.Models;
using BoxPilot.Core.Services;

namespace BoxPilot.Core.Infrastructure;

public static class CoreServiceHost
{
    private const int MaximumRemoteLogCharacters = 64 * 1024;

    public const string ModeArgument = "--boxpilot-service";

    public static bool IsInvocation(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0
               && string.Equals(arguments[0], ModeArgument, StringComparison.Ordinal);
    }

    public static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2 || !IsInvocation(arguments))
            return 64;
        if (!ProcessPrivileges.IsElevated())
            return 77;

        CoreServiceConfiguration configuration;
        CoreServiceLayout layout;
        try
        {
            configuration = LoadConfiguration(arguments[1]);
            layout = ValidateConfiguration(configuration, arguments[1]);
        }
        catch
        {
            return 78;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsServiceDispatcher.Run(
                configuration.ServiceName,
                cancellationToken => RunServiceAsync(configuration, layout, cancellationToken));
        }

        using var shutdown = new CancellationTokenSource();
        using var terminate = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
        using var interrupt = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
        return RunServiceAsync(configuration, layout, shutdown.Token).GetAwaiter().GetResult();
    }

    private static async Task<int> RunServiceAsync(
        CoreServiceConfiguration configuration,
        CoreServiceLayout layout,
        CancellationToken cancellationToken)
    {
        try
        {
            var coreFingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(
                    layout.CoreExecutablePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    coreFingerprint,
                    configuration.CoreFingerprint,
                    StringComparison.Ordinal))
            {
                return 78;
            }

            var paths = new AppPaths(configuration.DataRoot);
            await using var core = new SingBoxService(paths);
            await core.InitializeAsync(layout.CoreExecutablePath, cancellationToken).ConfigureAwait(false);
            await using var listener = CoreServiceTransport.CreateListener(configuration);
            while (!cancellationToken.IsCancellationRequested)
            {
                Stream connection;
                try
                {
                    connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await using (connection.ConfigureAwait(false))
                {
                    try
                    {
                        await HandleConnectionAsync(
                                connection,
                                configuration,
                                layout,
                                core,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        await StopCoreSafelyAsync(core).ConfigureAwait(false);
                        TryDelete(layout.RuntimeConfigurationPath);
                    }
                }
            }
            await StopCoreSafelyAsync(core).ConfigureAwait(false);
            TryDelete(layout.RuntimeConfigurationPath);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task HandleConnectionAsync(
        Stream connection,
        CoreServiceConfiguration configuration,
        CoreServiceLayout layout,
        SingBoxService core,
        CancellationToken serviceCancellation)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellation);
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            connectionCancellation.Token);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var writeGate = new SemaphoreSlim(1, 1);
        CoreServiceMessage? hello;
        try
        {
            hello = await CoreServiceProtocol.ReadAsync(connection, handshakeTimeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (hello?.Type != "hello"
            || hello.ProtocolVersion != CoreServiceProtocol.Version
            || hello.Token is null
            || !CoreServiceToken.MatchesHash(hello.Token, configuration.TokenHash))
        {
            return;
        }

        await CoreServiceProtocol.WriteAsync(
                connection,
                new CoreServiceMessage
                {
                    Type = "ready",
                    ProtocolVersion = CoreServiceProtocol.Version,
                    Success = true,
                    State = core.State,
                    ProcessId = core.ProcessId,
                    ApplicationFingerprint = configuration.ApplicationFingerprint,
                    CoreFingerprint = configuration.CoreFingerprint,
                },
                writeGate,
                connectionCancellation.Token)
            .ConfigureAwait(false);

        var logs = Channel.CreateBounded<CoreServiceMessage>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var logWriter = WriteLogsAsync(
            logs.Reader,
            connection,
            writeGate,
            connectionCancellation.Token);
        Action<CoreLogEntry> logHandler = entry => logs.Writer.TryWrite(new CoreServiceMessage
        {
            Type = "log",
            Timestamp = entry.Timestamp,
            Stream = entry.Stream,
            Message = entry.RawMessage.Length <= MaximumRemoteLogCharacters
                ? entry.RawMessage
                : entry.RawMessage[..MaximumRemoteLogCharacters] + "…",
        });
        Action<CoreStateChangedEventArgs> stateHandler = state => _ = WriteSafelyAsync(
            connection,
            new CoreServiceMessage
            {
                Type = "state",
                State = state.Current,
                ProcessId = state.ProcessId,
                Error = state.Error,
            },
            writeGate,
            connectionCancellation.Token);
        core.LogReceived += logHandler;
        core.StateChanged += stateHandler;

        try
        {
            while (!connectionCancellation.IsCancellationRequested)
            {
                var request = await CoreServiceProtocol.ReadAsync(
                        connection,
                        connectionCancellation.Token)
                    .ConfigureAwait(false);
                if (request is null)
                    break;
                if (request.Type != "request" || string.IsNullOrWhiteSpace(request.Command))
                    continue;

                CoreServiceMessage response;
                try
                {
                    await HandleRequestAsync(request, layout, core, connectionCancellation.Token)
                        .ConfigureAwait(false);
                    response = new CoreServiceMessage
                    {
                        Type = "response",
                        RequestId = request.RequestId,
                        Success = true,
                    };
                }
                catch (Exception exception)
                {
                    response = new CoreServiceMessage
                    {
                        Type = "response",
                        RequestId = request.RequestId,
                        Success = false,
                        Error = exception.Message,
                    };
                }

                await CoreServiceProtocol.WriteAsync(
                        connection,
                        response,
                        writeGate,
                        connectionCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            core.LogReceived -= logHandler;
            core.StateChanged -= stateHandler;
            await connectionCancellation.CancelAsync().ConfigureAwait(false);
            logs.Writer.TryComplete();
            try
            {
                await logWriter.ConfigureAwait(false);
            }
            catch
            {
            }
            // The GUI connection is the lease for TUN state, so a crash cannot leave routes behind.
            await StopCoreSafelyAsync(core).ConfigureAwait(false);
            TryDelete(layout.RuntimeConfigurationPath);
        }
    }

    private static async Task HandleRequestAsync(
        CoreServiceMessage request,
        CoreServiceLayout layout,
        SingBoxService core,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "start":
                if (string.IsNullOrWhiteSpace(request.Configuration)
                    || !TunConfiguration.ContainsTunInbound(request.Configuration))
                {
                    throw new InvalidDataException(
                        "The core service only starts configurations containing a TUN inbound.");
                }
                await AtomicFile.WriteAllTextAsync(
                        layout.RuntimeConfigurationPath,
                        request.Configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                var check = await core.CheckAsync(
                        layout.RuntimeConfigurationPath,
                        request.WorkingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!check.IsSuccess)
                    throw new InvalidDataException(check.CombinedOutput);
                await core.StartAsync(
                        layout.RuntimeConfigurationPath,
                        request.WorkingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stop":
                await core.StopAsync(cancellationToken).ConfigureAwait(false);
                TryDelete(layout.RuntimeConfigurationPath);
                break;
            default:
                throw new InvalidOperationException("The core service command is not supported.");
        }
    }

    private static async Task WriteLogsAsync(
        ChannelReader<CoreServiceMessage> logs,
        Stream connection,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        await foreach (var message in logs.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await CoreServiceProtocol.WriteAsync(
                    connection,
                    message,
                    writeGate,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteSafelyAsync(
        Stream connection,
        CoreServiceMessage message,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        try
        {
            await CoreServiceProtocol.WriteAsync(
                    connection,
                    message,
                    writeGate,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task StopCoreSafelyAsync(SingBoxService core)
    {
        try
        {
            await core.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static CoreServiceConfiguration LoadConfiguration(string path)
    {
        var json = File.ReadAllText(Path.GetFullPath(path), Utf8Text.Strict);
        return JsonSerializer.Deserialize(json, JsonDefaults.Context.CoreServiceConfiguration)
               ?? throw new InvalidDataException("The core service configuration is invalid.");
    }

    private static CoreServiceLayout ValidateConfiguration(
        CoreServiceConfiguration configuration,
        string configurationPath)
    {
        if (configuration.ProtocolVersion != CoreServiceProtocol.Version)
            throw new InvalidDataException("The core service protocol version is incompatible.");
        var layout = CoreServiceLayout.Create(
            new AppPaths(configuration.DataRoot),
            configuration.Identity);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetFullPath(configurationPath),
                layout.ConfigurationPath,
                comparison)
            || !string.Equals(configuration.ServiceName, layout.ServiceName, comparison)
            || !string.Equals(configuration.Endpoint, layout.Endpoint, comparison))
        {
            throw new InvalidDataException("The core service configuration path is not trusted.");
        }
        return layout;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
