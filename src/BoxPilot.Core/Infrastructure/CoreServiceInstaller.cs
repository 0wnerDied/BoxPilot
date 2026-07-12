using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

public static class CoreServiceInstaller
{
    private static readonly TimeSpan ElevatedOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstallerCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchDaemonStartupTimeout = TimeSpan.FromSeconds(10);

    public const string ModeArgument = "--boxpilot-service-install";
    public const string UninstallModeArgument = "--boxpilot-service-uninstall";
    public const string ResultPrefix = "boxpilot-installer-exit:";

    public static bool IsInvocation(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0
               && (string.Equals(arguments[0], ModeArgument, StringComparison.Ordinal)
                   || string.Equals(arguments[0], UninstallModeArgument, StringComparison.Ordinal));
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count != 3 || !IsInvocation(arguments))
            return 64;
        if (!ProcessPrivileges.IsElevated())
            return 77;

        try
        {
            var requestPath = Path.GetFullPath(arguments[1]);
            var requestFingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(
                    requestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(requestFingerprint, arguments[2], StringComparison.Ordinal))
                return 78;
            var requestJson = await Utf8Text.ReadAllTextAsync(requestPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(arguments[0], UninstallModeArgument, StringComparison.Ordinal))
            {
                var uninstallRequest = JsonSerializer.Deserialize(
                        requestJson,
                        JsonDefaults.Context.CoreServiceUninstallRequest)
                    ?? throw new InvalidDataException("The core service removal request is invalid.");
                await UninstallAsync(uninstallRequest, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var request = JsonSerializer.Deserialize(
                    requestJson,
                    JsonDefaults.Context.CoreServiceInstallRequest)
                ?? throw new InvalidDataException("The core service installation request is invalid.");
            await InstallAsync(request, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            return 70;
        }
    }

    internal static async Task InstallElevatedAsync(
        CoreServiceInstallRequest request,
        string requestPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        var json = JsonSerializer.Serialize(
            request,
            JsonDefaults.Context.CoreServiceInstallRequest);
        await WritePrivateFileAsync(requestPath, json, cancellationToken).ConfigureAwait(false);
        await RunElevatedOperationAsync(
                ModeArgument,
                requestPath,
                CoreServiceFailure.InstallationFailed,
                CoreServiceErrorCodes.InstallationFailed,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool IsInstalled(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var layout = CoreServiceLayout.Create(paths, CoreServiceIdentity.Create(paths));
        return File.Exists(layout.ConfigurationPath)
               && File.Exists(layout.ServiceExecutablePath)
               && File.Exists(layout.CoreExecutablePath);
    }

    public static async Task UninstallElevatedAsync(
        AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var request = new CoreServiceUninstallRequest
        {
            ProtocolVersion = CoreServiceProtocol.Version,
            Identity = CoreServiceIdentity.Create(paths),
            DataRoot = paths.RootDirectory,
        };
        paths.EnsureCreated();
        var requestPath = Path.Combine(
            paths.RuntimeDirectory,
            $"service-uninstall-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(
            request,
            JsonDefaults.Context.CoreServiceUninstallRequest);
        await WritePrivateFileAsync(requestPath, json, cancellationToken).ConfigureAwait(false);
        await RunElevatedOperationAsync(
                UninstallModeArgument,
                requestPath,
                CoreServiceFailure.RemovalFailed,
                CoreServiceErrorCodes.RemovalFailed,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RunElevatedOperationAsync(
        string mode,
        string requestPath,
        CoreServiceFailure failure,
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestFingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(
                    requestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            int exitCode;
            if (OperatingSystem.IsMacOS())
            {
                exitCode = await MacOSAuthorization.RunAsync(
                        CoreServiceFiles.ResolveApplicationExecutable(),
                        [mode, Path.GetFullPath(requestPath), requestFingerprint],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (OperatingSystem.IsWindows())
            {
                try
                {
                    exitCode = await RunWindowsElevatedOperationAsync(
                            mode,
                            requestPath,
                            requestFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw new CoreServiceException(failure, errorCode, exception);
                }
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            if (exitCode == 0)
                return;
            if (exitCode == 77)
            {
                throw new CoreServiceException(
                    CoreServiceFailure.AuthorizationDenied,
                    CoreServiceErrorCodes.AuthorizationDenied);
            }
            throw new CoreServiceException(failure, errorCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoreServiceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CoreServiceException(failure, errorCode, exception);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsElevatedOperationAsync(
        string mode,
        string requestPath,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        using var process = await LaunchWindowsAsync(
                mode,
                requestPath,
                requestFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ElevatedOperationTimeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("BoxPilot TUN installation did not finish in time.", exception);
        }
        return process.ExitCode;
    }

    private static async Task UninstallAsync(
        CoreServiceUninstallRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != CoreServiceProtocol.Version)
            throw new InvalidDataException("The core service protocol version is incompatible.");
        var paths = new AppPaths(request.DataRoot);
        var layout = CoreServiceLayout.Create(paths, request.Identity);
        if (layout.Platform == CoreServicePlatform.Windows)
        {
            await StopWindowsServiceAsync(layout.ServiceName, cancellationToken).ConfigureAwait(false);
            await RunCommandAsync(
                    "sc.exe",
                    ["delete", layout.ServiceName],
                    ignoreFailure: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await StopLaunchDaemonAsync(layout.ServiceName, cancellationToken).ConfigureAwait(false);
            TryDelete(layout.LaunchDaemonPath!);
            TryDelete(layout.Endpoint);
        }
        DeleteDirectoryWithRetry(layout.ServiceDirectory);
    }

    private static async Task InstallAsync(
        CoreServiceInstallRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var paths = new AppPaths(request.DataRoot);
        var layout = CoreServiceLayout.Create(paths, request.Identity);
        var payload = await CoreServiceFiles.ReadApplicationPayloadAsync(
                request.SourceApplicationPath,
                cancellationToken)
            .ConfigureAwait(false);
        var coreFingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(
                request.SourceCorePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                payload.Fingerprint,
                request.ApplicationFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                coreFingerprint,
                request.CoreFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The core service installation payload changed.");
        }

        if (layout.Platform == CoreServicePlatform.Windows)
            await StopWindowsServiceAsync(layout.ServiceName, cancellationToken).ConfigureAwait(false);
        else
            await StopLaunchDaemonAsync(layout.ServiceName, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(layout.ServiceDirectory);
        await ReplaceApplicationAsync(payload, layout, cancellationToken).ConfigureAwait(false);
        await ReplaceCoreAsync(request.SourceCorePath, layout.CoreExecutablePath, cancellationToken)
            .ConfigureAwait(false);
        // Rehash the protected copies to close the source-file race between validation and copying.
        var installedApplication = await CoreServiceFiles.ReadApplicationPayloadAsync(
                layout.ServiceExecutablePath,
                cancellationToken)
            .ConfigureAwait(false);
        var installedCoreFingerprint = await CoreServiceFiles.ComputeFileFingerprintAsync(
                layout.CoreExecutablePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                installedApplication.Fingerprint,
                request.ApplicationFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                installedCoreFingerprint,
                request.CoreFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The protected core service payload failed verification.");
        }

        var configuration = new CoreServiceConfiguration
        {
            ProtocolVersion = CoreServiceProtocol.Version,
            Identity = request.Identity,
            DataRoot = Path.GetFullPath(request.DataRoot),
            TokenHash = request.TokenHash,
            ApplicationFingerprint = request.ApplicationFingerprint,
            CoreFingerprint = request.CoreFingerprint,
            ServiceName = layout.ServiceName,
            Endpoint = layout.Endpoint,
            OwnerSid = request.OwnerSid,
            OwnerUid = request.OwnerUid,
        };
        var configurationJson = JsonSerializer.Serialize(
            configuration,
            JsonDefaults.Context.CoreServiceConfiguration);
        await WritePrivateFileAsync(
                layout.ConfigurationPath,
                configurationJson,
                cancellationToken)
            .ConfigureAwait(false);

        if (layout.Platform == CoreServicePlatform.Windows)
            await InstallWindowsServiceAsync(layout, cancellationToken).ConfigureAwait(false);
        else
            await InstallLaunchDaemonAsync(layout, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRequest(CoreServiceInstallRequest request)
    {
        if (request.ProtocolVersion != CoreServiceProtocol.Version)
            throw new InvalidDataException("The core service protocol version is incompatible.");
        _ = CoreServiceLayout.Create(new AppPaths(request.DataRoot), request.Identity);
        if (!File.Exists(request.SourceApplicationPath))
            throw new FileNotFoundException("The BoxPilot application payload is missing.");
        if (!File.Exists(request.SourceCorePath))
            throw new FileNotFoundException("The sing-box core payload is missing.");

        var expectedApplicationName = OperatingSystem.IsWindows() ? "BoxPilot.exe" : "BoxPilot";
        var expectedCoreName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        if (!string.Equals(
                Path.GetFileName(request.SourceApplicationPath),
                expectedApplicationName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(request.SourceCorePath),
                expectedCoreName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The core service payload names are invalid.");
        }
        if (request.TokenHash.Length != 64
            || request.TokenHash.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("The core service token hash is invalid.");
        }
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(request.OwnerSid))
            throw new InvalidDataException("The core service owner SID is missing.");
        var ownerKey = OperatingSystem.IsWindows()
            ? request.OwnerSid!
            : request.OwnerUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(
                request.Identity,
                CoreServiceIdentity.Create(ownerKey, request.DataRoot),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The core service owner identity is invalid.");
        }
    }

    private static async Task ReplaceApplicationAsync(
        CoreServiceApplicationPayload payload,
        CoreServiceLayout layout,
        CancellationToken cancellationToken)
    {
        var staging = $"{layout.ApplicationDirectory}.new-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var source in payload.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, Path.GetFileName(source));
                await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
            }

            DeleteDirectoryWithRetry(layout.ApplicationDirectory);
            Directory.Move(staging, layout.ApplicationDirectory);
            MakeExecutable(layout.ServiceExecutablePath);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task ReplaceCoreAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var staging = $"{destination}.new-{Guid.NewGuid():N}";
        try
        {
            await CopyFileAsync(source, staging, cancellationToken).ConfigureAwait(false);
            File.Move(staging, destination, overwrite: true);
            MakeExecutable(destination);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePrivateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.new-{Guid.NewGuid():N}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                var bytes = Utf8Text.Strict.GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task InstallLaunchDaemonAsync(
        CoreServiceLayout layout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        var executable = XmlEscape(layout.ServiceExecutablePath);
        var configuration = XmlEscape(layout.ConfigurationPath);
        var workingDirectory = XmlEscape(layout.ServiceDirectory);
        var label = XmlEscape(layout.ServiceName);
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{executable}</string>
                <string>{CoreServiceHost.ModeArgument}</string>
                <string>{configuration}</string>
              </array>
              <key>WorkingDirectory</key><string>{workingDirectory}</string>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>ProcessType</key><string>Background</string>
              <key>ThrottleInterval</key><integer>5</integer>
            </dict>
            </plist>
            """;
        await WritePrivateFileAsync(layout.LaunchDaemonPath!, plist, cancellationToken)
            .ConfigureAwait(false);
        File.SetUnixFileMode(
            layout.LaunchDaemonPath!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await RunCommandAsync(
                "/bin/launchctl",
                ["bootstrap", "system", layout.LaunchDaemonPath!],
                ignoreFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
        await RunCommandAsync(
                "/bin/launchctl",
                ["kickstart", "-k", $"system/{layout.ServiceName}"],
                ignoreFailure: false,
                cancellationToken)
            .ConfigureAwait(false);

        var attempts = (int)(LaunchDaemonStartupTimeout / TimeSpan.FromMilliseconds(250));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (File.Exists(layout.Endpoint))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("BoxPilot TUN did not start its system service.");
    }

    private static Task StopLaunchDaemonAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        return RunCommandAsync(
            "/bin/launchctl",
            ["bootout", $"system/{serviceName}"],
            ignoreFailure: true,
            cancellationToken);
    }

    private static async Task InstallWindowsServiceAsync(
        CoreServiceLayout layout,
        CancellationToken cancellationToken)
    {
        var binaryPath = $"\"{layout.ServiceExecutablePath}\" {CoreServiceHost.ModeArgument} "
                         + $"\"{layout.ConfigurationPath}\"";
        var query = await RunCommandAsync(
                "sc.exe",
                ["query", layout.ServiceName],
                ignoreFailure: true,
                cancellationToken)
            .ConfigureAwait(false);
        var verb = query.ExitCode == 0 ? "config" : "create";
        await RunCommandAsync(
                "sc.exe",
                [
                    verb,
                    layout.ServiceName,
                    "binPath=", binaryPath,
                    "start=", "auto",
                    "DisplayName=", "BoxPilot TUN Service",
                ],
                ignoreFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
        await RunCommandAsync(
                "sc.exe",
                ["description", layout.ServiceName, "Runs sing-box with protected TUN privileges for BoxPilot."],
                ignoreFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
        await RunCommandAsync(
                "sc.exe",
                ["failure", layout.ServiceName, "reset=", "86400", "actions=", "restart/5000"],
                ignoreFailure: true,
                cancellationToken)
            .ConfigureAwait(false);
        await RunCommandAsync(
                "sc.exe",
                ["start", layout.ServiceName],
                ignoreFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task StopWindowsServiceAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        await RunCommandAsync(
                "sc.exe",
                ["stop", serviceName],
                ignoreFailure: true,
                cancellationToken)
            .ConfigureAwait(false);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await RunCommandAsync(
                    "sc.exe",
                    ["query", serviceName],
                    ignoreFailure: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0
                || result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ProcessResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool ignoreFailure,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(InstallerCommandTimeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"{Path.GetFileName(executable)} did not finish in time.",
                exception);
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {output}{error}".Trim());
        }
        return new ProcessResult(process.ExitCode, output + error);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<Process> LaunchWindowsAsync(
        string mode,
        string requestPath,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Process>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(LaunchWindows(mode, requestPath, requestFingerprint));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "BoxPilot service installer",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new CoreServiceException(
                CoreServiceFailure.AuthorizationDenied,
                CoreServiceErrorCodes.AuthorizationDenied,
                exception);
        }
    }

    private static Process LaunchWindows(
        string mode,
        string requestPath,
        string requestFingerprint)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CoreServiceFiles.ResolveApplicationExecutable(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(Path.GetFullPath(requestPath));
        startInfo.ArgumentList.Add(requestFingerprint);
        return Process.Start(startInfo)
               ?? throw new CoreServiceException(
                   CoreServiceFailure.Unavailable,
                   CoreServiceErrorCodes.Unavailable);
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
            return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
    }

    private static string XmlEscape(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    private static void TryKill(Process process)
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
