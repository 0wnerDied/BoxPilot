using System.Text;

namespace BoxPilot.Core.Infrastructure;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Utf8WithoutBom, cancellationToken)
                .ConfigureAwait(false);
            RestrictFilePermissions(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
