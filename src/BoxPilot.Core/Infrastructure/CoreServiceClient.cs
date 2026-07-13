using System.Collections.Concurrent;
using System.Net.Sockets;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal interface ICoreServiceClient : IAsyncDisposable
{
    event Action<CoreLogEntry>? LogReceived;

    event Action<CoreStateChangedEventArgs>? StateChanged;

    event Action<string>? Disconnected;

    Task StartAsync(
        string executablePath,
        string configurationPath,
        string? workingDirectory,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class CoreServiceClient(AppPaths paths) : ICoreServiceClient
{
    private static readonly TimeSpan InitialConnectionTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InstalledConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(1);
    private readonly CoreServiceLayout layout = CoreServiceLayout.Create(
        paths,
        CoreServiceIdentity.Create(paths));
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CoreServiceMessage>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private Stream? connection;
    private Task? readerTask;
    private CoreServiceApplicationPayload? applicationPayload;
    private string? fingerprintedCorePath;
    private long fingerprintedCoreLength;
    private DateTime fingerprintedCoreWriteTimeUtc;
    private string? cachedCoreFingerprint;
    private string? connectedApplicationFingerprint;
    private string? connectedCoreFingerprint;
    private long nextRequestId;
    private bool disposed;
    private bool expectedDisconnect;

    public event Action<CoreLogEntry>? LogReceived;

    public event Action<CoreStateChangedEventArgs>? StateChanged;

    public event Action<string>? Disconnected;

    private bool IsConnected => connection is not null;

    public async Task StartAsync(
        string executablePath,
        string configurationPath,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        EnsureCacheDatabaseExists();
        var configuration = await Utf8Text.ReadAllTextAsync(
                Path.GetFullPath(configurationPath),
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureConnectedAsync(executablePath, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendRequestAsync(
                    "start",
                    configuration,
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
            return;
        await SendRequestAsync("stop", null, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        // Closing the authenticated lease makes the service stop TUN even if IPC is unresponsive.
        await lifetime.CancelAsync().ConfigureAwait(false);
        var activeConnection = Interlocked.Exchange(ref connection, null);
        if (activeConnection is not null)
            await activeConnection.DisposeAsync().ConfigureAwait(false);
        ClearConnectionState();
        FailPendingRequests();
        if (readerTask is not null)
        {
            try
            {
                await readerTask.WaitAsync(ReaderShutdownTimeout).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        lifetime.Dispose();
        connectionGate.Dispose();
        writeGate.Dispose();
    }

    private async Task SendRequestAsync(
        string command,
        string? configuration,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        await RunWithTimeoutAsync(async requestCancellation =>
        {
            var stream = connection
                ?? throw new CoreServiceException(
                    CoreServiceFailure.Unavailable,
                    CoreServiceErrorCodes.Unavailable);
            var requestId = Interlocked.Increment(ref nextRequestId);
            var completion = new TaskCompletionSource<CoreServiceMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending[requestId] = completion;

            try
            {
                await CoreServiceProtocol.WriteAsync(
                        stream,
                        new CoreServiceMessage
                        {
                            Type = "request",
                            RequestId = requestId,
                            Command = command,
                            Configuration = configuration,
                            WorkingDirectory = workingDirectory,
                        },
                        writeGate,
                        requestCancellation)
                    .ConfigureAwait(false);
                var response = await completion.Task.WaitAsync(requestCancellation).ConfigureAwait(false);
                if (!response.Success)
                    throw new InvalidOperationException(response.Error ?? "The core service request failed.");
            }
            catch (IOException exception)
            {
                throw new CoreServiceException(
                    CoreServiceFailure.Unavailable,
                    CoreServiceErrorCodes.Disconnected,
                    exception);
            }
            finally
            {
                pending.TryRemove(requestId, out _);
            }
        }, RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoreServiceException(
                CoreServiceFailure.Unavailable,
                CoreServiceErrorCodes.Unavailable,
                exception);
        }
    }

    private async Task EnsureConnectedAsync(
        string coreExecutablePath,
        CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            paths.EnsureCreated();
            var token = await CoreServiceToken.GetOrCreateAsync(layout.TokenPath, cancellationToken)
                .ConfigureAwait(false);
            var application = applicationPayload ??= await CoreServiceFiles.ReadApplicationPayloadAsync(
                    CoreServiceFiles.ResolveApplicationExecutable(),
                    cancellationToken)
                .ConfigureAwait(false);
            var corePath = Path.GetFullPath(coreExecutablePath);
            var coreFingerprint = await GetCoreFingerprintAsync(corePath, cancellationToken)
                .ConfigureAwait(false);
            if (IsConnected
                && string.Equals(
                    connectedApplicationFingerprint,
                    application.Fingerprint,
                    StringComparison.Ordinal)
                && string.Equals(connectedCoreFingerprint, coreFingerprint, StringComparison.Ordinal))
            {
                return;
            }
            var connectedServiceNeedsUpdate = IsConnected;
            if (connectedServiceNeedsUpdate)
                await DetachAsync().ConfigureAwait(false);

            var opened = connectedServiceNeedsUpdate
                ? null
                : await TryOpenAsync(
                        token,
                        application.Fingerprint,
                        coreFingerprint,
                        InitialConnectionTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (opened is not null)
            {
                Attach(opened.Value.Stream, opened.Value.Ready);
                return;
            }

            var request = new CoreServiceInstallRequest
            {
                ProtocolVersion = CoreServiceProtocol.Version,
                Identity = layout.Identity,
                DataRoot = paths.RootDirectory,
                SourceApplicationPath = application.ExecutablePath,
                SourceCorePath = corePath,
                ApplicationFingerprint = application.Fingerprint,
                CoreFingerprint = coreFingerprint,
                TokenHash = CoreServiceToken.Hash(token),
                OwnerSid = CoreServiceIdentity.GetOwnerSid(),
                OwnerUid = ProcessPrivileges.GetUserId(),
            };
            // Elevation is confined to installation; normal lifecycle requests never carry an executable path.
            var requestPath = Path.Combine(
                paths.RuntimeDirectory,
                $"service-install-{Guid.NewGuid():N}.json");
            await CoreServiceInstaller.InstallElevatedAsync(
                    request,
                    requestPath,
                    cancellationToken)
                .ConfigureAwait(false);

            opened = await TryOpenAsync(
                    token,
                    application.Fingerprint,
                    coreFingerprint,
                    InstalledConnectionTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (opened is null)
            {
                throw new CoreServiceException(
                    CoreServiceFailure.Unavailable,
                    CoreServiceErrorCodes.Unavailable);
            }
            Attach(opened.Value.Stream, opened.Value.Ready);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<(Stream Stream, CoreServiceMessage Ready)?> TryOpenAsync(
        string token,
        string applicationFingerprint,
        string coreFingerprint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                stream = await CoreServiceTransport.ConnectAsync(layout, attempt.Token)
                    .ConfigureAwait(false);
                await CoreServiceProtocol.WriteAsync(
                        stream,
                        new CoreServiceMessage
                        {
                            Type = "hello",
                            ProtocolVersion = CoreServiceProtocol.Version,
                            Token = token,
                        },
                        writeGate,
                        attempt.Token)
                    .ConfigureAwait(false);
                var ready = await CoreServiceProtocol.ReadAsync(stream, attempt.Token)
                    .ConfigureAwait(false);
                if (ready?.Type == "ready"
                    && ready.Success
                    && ready.ProtocolVersion == CoreServiceProtocol.Version
                    && string.Equals(
                        ready.ApplicationFingerprint,
                        applicationFingerprint,
                        StringComparison.Ordinal)
                    && string.Equals(
                        ready.CoreFingerprint,
                        coreFingerprint,
                        StringComparison.Ordinal))
                {
                    var connectedStream = stream;
                    stream = null;
                    return (connectedStream, ready);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private void Attach(Stream stream, CoreServiceMessage ready)
    {
        connection = stream;
        connectedApplicationFingerprint = ready.ApplicationFingerprint;
        connectedCoreFingerprint = ready.CoreFingerprint;
        readerTask = ReadLoopAsync(stream, lifetime.Token);
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await CoreServiceProtocol.ReadAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                    break;

                switch (message.Type)
                {
                    case "response":
                        if (pending.TryGetValue(message.RequestId, out var completion))
                            completion.TrySetResult(message);
                        break;
                    case "state":
                        StateChanged?.Invoke(new CoreStateChangedEventArgs(
                            message.State,
                            message.ProcessId,
                            message.Error));
                        break;
                    case "log" when message.Message is not null:
                        LogReceived?.Invoke(CoreLogParser.Parse(
                            message.Timestamp,
                            message.Stream,
                            message.Message));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            var wasCurrent = ReferenceEquals(
                Interlocked.CompareExchange(ref connection, null, stream),
                stream);
            if (wasCurrent)
            {
                ClearConnectionState();
                FailPendingRequests();
                if (!disposed && !expectedDisconnect && !cancellationToken.IsCancellationRequested)
                    Disconnected?.Invoke(CoreServiceErrorCodes.Disconnected);
            }
        }
    }

    private async Task DetachAsync()
    {
        expectedDisconnect = true;
        try
        {
            var activeConnection = Interlocked.Exchange(ref connection, null);
            if (activeConnection is not null)
                await activeConnection.DisposeAsync().ConfigureAwait(false);
            ClearConnectionState();
            FailPendingRequests();
            if (readerTask is not null)
            {
                try
                {
                    await readerTask.WaitAsync(ReaderShutdownTimeout).ConfigureAwait(false);
                }
                catch
                {
                }
                readerTask = null;
            }
        }
        finally
        {
            expectedDisconnect = false;
        }
    }

    private void ClearConnectionState()
    {
        connectedApplicationFingerprint = null;
        connectedCoreFingerprint = null;
    }

    private void FailPendingRequests()
    {
        var failure = new IOException("The core service disconnected.");
        foreach (var completion in pending.Values)
            completion.TrySetException(failure);
    }

    private void EnsureCacheDatabaseExists()
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.CacheDirectory, "sing-box.db");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }

    private async Task<string> GetCoreFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (cachedCoreFingerprint is not null
            && string.Equals(path, fingerprintedCorePath, PathComparison)
            && info.Length == fingerprintedCoreLength
            && info.LastWriteTimeUtc == fingerprintedCoreWriteTimeUtc)
        {
            return cachedCoreFingerprint;
        }

        var fingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(path, cancellationToken)
            .ConfigureAwait(false);
        fingerprintedCorePath = path;
        fingerprintedCoreLength = info.Length;
        fingerprintedCoreWriteTimeUtc = info.LastWriteTimeUtc;
        cachedCoreFingerprint = fingerprint;
        return fingerprint;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
