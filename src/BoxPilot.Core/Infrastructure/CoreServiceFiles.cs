using System.Security.Cryptography;
using System.Text;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal sealed record CoreServiceApplicationPayload(
    string ExecutablePath,
    IReadOnlyList<string> Files,
    string Fingerprint);

internal static class CoreServiceFiles
{
    public static string ResolveApplicationExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "BoxPilot.exe" : "BoxPilot";
        var candidate = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(
                Path.GetFileName(processPath),
                executableName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }

        throw new CoreServiceException(
            CoreServiceFailure.Unavailable,
            "BoxPilot could not locate its service executable.");
    }

    public static async Task<CoreServiceApplicationPayload> ReadApplicationPayloadAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var executable = Path.GetFullPath(executablePath);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The BoxPilot service executable was not found.", executable);

        var directory = Path.GetDirectoryName(executable)
            ?? throw new InvalidDataException("The BoxPilot executable directory is invalid.");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => !string.Equals(
                Path.GetExtension(path),
                ".pdb",
                StringComparison.OrdinalIgnoreCase))
            .Where(static path => !string.Equals(
                Path.GetExtension(path),
                ".xml",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (!files.Contains(executable, PathComparer))
            throw new InvalidDataException("The BoxPilot executable is outside its payload directory.");

        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            fingerprint.AppendData(Encoding.UTF8.GetBytes(name));
            fingerprint.AppendData([0]);
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            fingerprint.AppendData(hash);
        }

        return new CoreServiceApplicationPayload(
            executable,
            files,
            Convert.ToHexStringLower(fingerprint.GetHashAndReset()));
    }

    public static async Task<string> ComputeFileFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
