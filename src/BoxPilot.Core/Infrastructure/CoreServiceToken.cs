using System.Security.Cryptography;

namespace BoxPilot.Core.Infrastructure;

internal static class CoreServiceToken
{
    public static async Task<string> GetOrCreateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            var existing = (await Utf8Text.ReadAllTextAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false))
                .Trim();
            if (IsValid(existing))
                return existing;
        }
        catch (FileNotFoundException)
        {
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using var stream = new FileStream(fullPath, options);
        var bytes = Utf8Text.Strict.GetBytes(token);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Utf8Text.Strict.GetBytes(token)));
    }

    public static bool MatchesHash(string token, string expectedHash)
    {
        var actual = Convert.FromHexString(Hash(token));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
        return actual.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsValid(string value)
    {
        return value.Length == 64
               && value.All(static character => char.IsAsciiHexDigit(character));
    }
}
