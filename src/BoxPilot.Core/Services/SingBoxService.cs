using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class SingBoxService(AppPaths paths) : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private CancellationTokenSource? logCancellation;
    private readonly Func<ICoreServiceClient> coreServiceFactory = () => new CoreServiceClient(paths);
    private readonly Func<bool> isElevated = ProcessPrivileges.IsElevated;
    private ICoreServiceClient? coreService;
    private string executablePath = string.Empty;
    private bool serviceCoreActive;
    private int? serviceProcessId;
    private CoreState state = CoreState.Stopped;

    internal SingBoxService(
        AppPaths paths,
        Func<ICoreServiceClient> coreServiceFactory,
        Func<bool> isElevated)
        : this(paths)
    {
        this.coreServiceFactory = coreServiceFactory;
        this.isElevated = isElevated;
    }

    public event Action<CoreLogEntry>? LogReceived;

    public event Action<CoreStateChangedEventArgs>? StateChanged;

    public CoreState State => state;

    public int? ProcessId
    {
        get
        {
            if (serviceCoreActive)
                return serviceProcessId;

            try
            {
                return process is { HasExited: false } runningProcess
                    ? runningProcess.Id
                    : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool IsCoreServiceInstalled => CoreServiceInstaller.IsInstalled(paths);

    private static string ResolveExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expanded))
                return Path.GetFullPath(expanded);
        }

        var executableName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "core", executableName),
            Path.Combine(AppContext.BaseDirectory, executableName),
        };

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/opt/homebrew/bin/sing-box");
            candidates.Add("/usr/local/bin/sing-box");
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "sing-box",
                executableName));
        }

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        candidates.AddRange(pathDirectories.Select(directory => Path.Combine(directory, executableName)));

        var match = candidates.FirstOrDefault(File.Exists);
        if (match is null)
        {
            throw new FileNotFoundException(
                "sing-box was not found. Set its path in BoxPilot settings or add it to PATH.");
        }

        return Path.GetFullPath(match);
    }

    public async Task<string> InitializeAsync(
        string? configuredPath = null,
        CancellationToken cancellationToken = default)
    {
        executablePath = ResolveExecutable(configuredPath);
        var result = await RunCommandAsync(["version"], TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.CombinedOutput);

        return ParseVersion(result.StandardOutput);
    }

    public Task<CommandResult> CheckAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        return RunCommandAsync(
            ["check", "-c", Path.GetFullPath(configurationPath)],
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public Task<CommandResult> RunToolAsync(
        string commandLine,
        CancellationToken cancellationToken = default)
    {
        var arguments = CommandLineTokenizer.Split(commandLine);
        if (arguments.Count == 0)
            throw new ArgumentException("Enter a sing-box command.", nameof(commandLine));
        if (string.Equals(arguments[0], "run", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use BoxPilot's Start action to run the managed core.");

        return RunCommandAsync(arguments, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task StartAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        var operationCancellation = operation.Token;

        await lifecycleGate.WaitAsync(operationCancellation).ConfigureAwait(false);
        try
        {
            if (process is { HasExited: false } || serviceCoreActive)
                return;
            if (string.IsNullOrWhiteSpace(executablePath))
                await InitializeAsync(cancellationToken: operationCancellation).ConfigureAwait(false);

            SetState(CoreState.Starting);
            paths.EnsureCreated();
            var hasTunInbound = await TunConfiguration.ContainsTunInboundAsync(
                    configurationPath,
                    operationCancellation)
                .ConfigureAwait(false);
            var requiresService = hasTunInbound && !isElevated();
            if (requiresService)
            {
                var client = GetCoreService();
                serviceCoreActive = true;
                try
                {
                    await client.StartAsync(
                            executablePath,
                            Path.GetFullPath(configurationPath),
                            operationCancellation)
                        .ConfigureAwait(false);
                    if (state != CoreState.Running)
                        SetState(CoreState.Running, serviceProcessId);
                    return;
                }
                catch
                {
                    serviceCoreActive = false;
                    serviceProcessId = null;
                    throw;
                }
            }

            await ReleaseCoreServiceAsync().ConfigureAwait(false);

            var startInfo = CreateStartInfo(["run", "-c", Path.GetFullPath(configurationPath)]);
            startInfo.WorkingDirectory = paths.RuntimeDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var newProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            newProcess.Exited += OnProcessExited;
            process = newProcess;

            if (!newProcess.Start())
                throw new InvalidOperationException("sing-box did not start.");

            logCancellation = new CancellationTokenSource();
            _ = ReadLinesAsync(
                newProcess.StandardOutput,
                CoreLogStream.StandardOutput,
                logCancellation.Token);
            _ = ReadLinesAsync(
                newProcess.StandardError,
                CoreLogStream.StandardError,
                logCancellation.Token);

            SetState(CoreState.Running, newProcess.Id);
            WriteLog(CoreLogStream.BoxPilot, $"Started sing-box process {newProcess.Id}.");
        }
        catch (OperationCanceledException) when (
            lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CleanupProcess();
            SetState(CoreState.Stopped);
            throw;
        }
        catch (Exception exception)
        {
            CleanupProcess();
            SetState(
                CoreState.Faulted,
                error: exception is CoreServiceException ? null : exception.Message);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? runningProcess;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (serviceCoreActive)
            {
                SetState(CoreState.Stopping, serviceProcessId);
                try
                {
                    await coreService!.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    serviceCoreActive = false;
                    serviceProcessId = null;
                }
                SetState(CoreState.Stopped);
                return;
            }

            runningProcess = process;
            if (runningProcess is null || runningProcess.HasExited)
            {
                CleanupProcess();
                SetState(CoreState.Stopped);
                return;
            }

            SetState(CoreState.Stopping, runningProcess.Id);
            RequestGracefulStop(runningProcess);
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await runningProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!runningProcess.HasExited)
                runningProcess.Kill(true);
            await runningProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(process, runningProcess))
                CleanupProcess();
            SetState(CoreState.Stopped);
            WriteLog(CoreLogStream.BoxPilot, "sing-box stopped.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(configurationPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task UninstallCoreServiceAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await ReleaseCoreServiceAsync().ConfigureAwait(false);
        await CoreServiceInstaller.UninstallElevatedAsync(paths, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CommandResult> RunCommandAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            executablePath = ResolveExecutable();

        using var commandProcess = new Process
        {
            StartInfo = CreateStartInfo(arguments),
        };
        commandProcess.StartInfo.RedirectStandardOutput = true;
        commandProcess.StartInfo.RedirectStandardError = true;

        if (!commandProcess.Start())
            throw new InvalidOperationException("Unable to start sing-box.");

        var standardOutputTask = commandProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = commandProcess.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await commandProcess.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!commandProcess.HasExited)
                commandProcess.Kill(true);
            throw;
        }

        return new CommandResult(
            commandProcess.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            ForceStopProcess();
            serviceCoreActive = false;
            serviceProcessId = null;
            SetState(CoreState.Stopped);
        }

        await ReleaseCoreServiceAsync().ConfigureAwait(false);
        lifetime.Dispose();
        lifecycleGate.Dispose();
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8Text.Strict,
            StandardErrorEncoding = Utf8Text.Strict,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private async Task ReadLinesAsync(
        StreamReader reader,
        CoreLogStream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                WriteLog(stream, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        try
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(process, sender))
                    return;

                var exitCode = process?.ExitCode;
                CleanupProcess();
                if (state == CoreState.Stopping || exitCode == 0)
                    SetState(CoreState.Stopped);
                else
                    SetState(CoreState.Faulted, error: $"sing-box exited with code {exitCode}.");
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CleanupProcess()
    {
        logCancellation?.Cancel();
        logCancellation?.Dispose();
        logCancellation = null;

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
            process = null;
        }
    }

    private void ForceStopProcess()
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        CleanupProcess();
    }

    private ICoreServiceClient GetCoreService()
    {
        if (coreService is not null)
            return coreService;

        coreService = coreServiceFactory();
        coreService.LogReceived += OnCoreServiceLogReceived;
        coreService.StateChanged += OnServiceStateChanged;
        coreService.Disconnected += OnCoreServiceDisconnected;
        return coreService;
    }

    private async ValueTask ReleaseCoreServiceAsync()
    {
        var client = coreService;
        if (client is null)
            return;

        coreService = null;
        client.LogReceived -= OnCoreServiceLogReceived;
        client.StateChanged -= OnServiceStateChanged;
        client.Disconnected -= OnCoreServiceDisconnected;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    private void OnCoreServiceLogReceived(CoreLogEntry entry)
    {
        LogReceived?.Invoke(entry);
    }

    private void OnServiceStateChanged(CoreStateChangedEventArgs eventArgs)
    {
        if (!serviceCoreActive)
            return;

        serviceProcessId = eventArgs.ProcessId;
        if (eventArgs.Current is CoreState.Stopped or CoreState.Faulted)
        {
            serviceCoreActive = false;
            serviceProcessId = null;
        }
        SetState(eventArgs.Current, eventArgs.ProcessId, eventArgs.Error);
    }

    private void OnCoreServiceDisconnected(string error)
    {
        if (!serviceCoreActive)
            return;

        serviceCoreActive = false;
        serviceProcessId = null;
        if (state != CoreState.Stopped)
            SetState(CoreState.Faulted, error: error);
    }

    private void SetState(CoreState newState, int? processId = null, string? error = null)
    {
        if (state == newState && error is null)
            return;

        state = newState;
        StateChanged?.Invoke(new CoreStateChangedEventArgs(newState, processId, error));
    }

    private void WriteLog(CoreLogStream stream, string message)
    {
        LogReceived?.Invoke(CoreLogParser.Parse(DateTimeOffset.Now, stream, message));
    }

    private static void RequestGracefulStop(Process runningProcess)
    {
        if (OperatingSystem.IsWindows())
        {
            runningProcess.CloseMainWindow();
            return;
        }

        if (Kill(runningProcess.Id, 15) != 0 && !runningProcess.HasExited)
            runningProcess.Kill();
    }

    private static string ParseVersion(string output)
    {
        var version = Regex.Match(output, @"sing-box version (?<value>[^\s]+)")
            .Groups["value"].Value;
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
