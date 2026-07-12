using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class SingBoxService(AppPaths paths) : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private Process? process;
    private CancellationTokenSource? logCancellation;
    private CoreState state = CoreState.Stopped;

    public event Action<CoreLogEntry>? LogReceived;

    public event Action<CoreStateChangedEventArgs>? StateChanged;

    public CoreState State => state;

    public int? ProcessId
    {
        get
        {
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

    public string ExecutablePath { get; private set; } = string.Empty;

    public string ResolveExecutable(string? configuredPath = null)
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

    public async Task<CoreVersionInfo> InitializeAsync(
        string? configuredPath = null,
        CancellationToken cancellationToken = default)
    {
        ExecutablePath = ResolveExecutable(configuredPath);
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

    public Task<CommandResult> FormatAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        return RunCommandAsync(
            ["format", "-c", Path.GetFullPath(configurationPath)],
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

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is { HasExited: false })
                return;
            if (string.IsNullOrWhiteSpace(ExecutablePath))
                await InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            SetState(CoreState.Starting);
            paths.EnsureCreated();

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

            if (!newProcess.Start())
                throw new InvalidOperationException("sing-box did not start.");

            process = newProcess;
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
        catch (Exception exception)
        {
            CleanupProcess();
            SetState(CoreState.Faulted, error: exception.Message);
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

    public async Task<CommandResult> RunCommandAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
            ExecutablePath = ResolveExecutable();

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
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            CleanupProcess();
        }

        lifecycleGate.Dispose();
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
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

    private void SetState(CoreState newState, int? processId = null, string? error = null)
    {
        if (state == newState && error is null)
            return;

        var previous = state;
        state = newState;
        StateChanged?.Invoke(new CoreStateChangedEventArgs(previous, newState, processId, error));
    }

    private void WriteLog(CoreLogStream stream, string message)
    {
        LogReceived?.Invoke(new CoreLogEntry(DateTimeOffset.Now, stream, message));
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

    private static CoreVersionInfo ParseVersion(string output)
    {
        var version = Regex.Match(output, @"sing-box version (?<value>[^\s]+)")
            .Groups["value"].Value;
        var environment = Regex.Match(output, @"Environment:\s+\S+\s+(?<value>[^\r\n]+)")
            .Groups["value"].Value.Trim();
        var tagsValue = Regex.Match(output, @"Tags:\s*(?<value>[^\r\n]*)")
            .Groups["value"].Value;
        var tags = tagsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CoreVersionInfo(
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            environment,
            tags,
            output.Trim());
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
