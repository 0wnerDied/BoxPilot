using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BoxPilot.Core.Infrastructure;

internal enum CoreServicePlatform
{
    MacOS,
    Windows,
}

internal sealed record CoreServiceLayout(
    CoreServicePlatform Platform,
    string Identity,
    string ServiceName,
    string ServiceDirectory,
    string ApplicationDirectory,
    string ServiceExecutablePath,
    string CoreExecutablePath,
    string ConfigurationPath,
    string RuntimeConfigurationPath,
    string Endpoint,
    string TokenPath,
    string? LaunchDaemonPath)
{
    private const string MacOSRoot = "/Library/Application Support/BoxPilot/Services";

    public static CoreServiceLayout Create(AppPaths paths, string identity)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateIdentity(identity);

        if (OperatingSystem.IsMacOS())
        {
            return Create(
                CoreServicePlatform.MacOS,
                identity,
                paths.RootDirectory,
                MacOSRoot,
                "/var/run/boxpilot",
                "/Library/LaunchDaemons");
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles))
                throw new PlatformNotSupportedException("The Windows Program Files directory is unavailable.");
            return Create(
                CoreServicePlatform.Windows,
                identity,
                paths.RootDirectory,
                Path.Combine(programFiles, "BoxPilot", "Services"),
                string.Empty,
                string.Empty);
        }

        throw new PlatformNotSupportedException(
            "The BoxPilot core service is supported only on macOS and Windows.");
    }

    internal static CoreServiceLayout Create(
        CoreServicePlatform platform,
        string identity,
        string dataRoot,
        string serviceRoot,
        string socketRoot,
        string launchDaemonRoot)
    {
        ValidateIdentity(identity);
        var serviceName = platform == CoreServicePlatform.Windows
            ? $"BoxPilotCore_{identity}"
            : $"tech.0b1t.boxpilot.core.{identity}";
        var serviceDirectory = Path.Combine(serviceRoot, identity);
        var applicationDirectory = Path.Combine(serviceDirectory, "app");
        var executableName = platform == CoreServicePlatform.Windows ? "BoxPilot.exe" : "BoxPilot";
        var coreName = platform == CoreServicePlatform.Windows ? "sing-box.exe" : "sing-box";
        var endpoint = platform == CoreServicePlatform.Windows
            ? $"BoxPilot.Core.{identity}"
            : Path.Combine(socketRoot, $"{identity}.sock");

        return new CoreServiceLayout(
            platform,
            identity,
            serviceName,
            serviceDirectory,
            applicationDirectory,
            Path.Combine(applicationDirectory, executableName),
            Path.Combine(serviceDirectory, coreName),
            Path.Combine(serviceDirectory, "service.json"),
            Path.Combine(serviceDirectory, "runtime.json"),
            endpoint,
            Path.Combine(Path.GetFullPath(dataRoot), "service.token"),
            platform == CoreServicePlatform.MacOS
                ? Path.Combine(launchDaemonRoot, $"{serviceName}.plist")
                : null);
    }

    private static void ValidateIdentity(string identity)
    {
        if (identity.Length != 16
            || identity.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("The core service identity is invalid.");
        }
    }
}

internal static class CoreServiceIdentity
{
    public static string Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Create(GetOwnerKey(), paths.RootDirectory);
    }

    internal static string Create(string ownerKey, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var input = Encoding.UTF8.GetBytes($"{ownerKey}\0{normalizedRoot}");
        return Convert.ToHexStringLower(SHA256.HashData(input))[..16];
    }

    public static string? GetOwnerSid()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return WindowsIdentity.GetCurrent().User?.Value;
    }

    private static string GetOwnerKey()
    {
        return OperatingSystem.IsWindows()
            ? GetOwnerSid() ?? throw new InvalidOperationException("The Windows user SID is unavailable.")
            : ProcessPrivileges.GetUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
