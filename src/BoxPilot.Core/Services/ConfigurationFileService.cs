using System.Text.Json.Nodes;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class ConfigurationFileService(
    SingBoxConfigService configService,
    ProfileRepository profileRepository)
{
    private const long MaximumConfigurationSize = 16 * 1024 * 1024;

    public async Task<Profile> ImportAsync(
        string? name,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one configuration is required.", nameof(sourcePaths));

        var sources = sourcePaths.Select(path => new FileInfo(Path.GetFullPath(path))).ToArray();
        foreach (var source in sources)
        {
            if (!source.Exists)
                throw new FileNotFoundException("The sing-box configuration does not exist.", source.FullName);
            if (!string.Equals(source.Extension, ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A sing-box configuration must use the .json extension.");
        }
        if (sources.Sum(static source => source.Length) > MaximumConfigurationSize)
            throw new InvalidDataException("The sing-box configuration exceeds the 16 MiB import limit.");

        var workingDirectories = sources.Select(static source => source.DirectoryName).Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        var workingDirectory = workingDirectories.Length == 1
            ? workingDirectories[0]
            : throw new InvalidDataException("Configuration fragments must be stored in the same directory.");
        workingDirectory = workingDirectory
                               ?? throw new InvalidDataException(
                                   "The sing-box configuration has no working directory.");
        var configuration = sources.Length == 1
            ? await Utf8Text.ReadAllTextAsync(sources[0].FullName, cancellationToken).ConfigureAwait(false)
            : await configService.MergeAsync(
                    sources.Select(static source => source.FullName).ToArray(),
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        var parsed = configService.Parse(configuration);
        await configService.ValidateOrThrowAsync(
                configuration,
                workingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        var profileName = string.IsNullOrWhiteSpace(name)
            ? sources.Length == 1
                ? Path.GetFileNameWithoutExtension(sources[0].Name)
                : new DirectoryInfo(workingDirectory).Name
            : name.Trim();
        var profile = await profileRepository.CreateAsync(
                profileName,
                configuration,
                ProfileSource.ImportedFile,
                cancellationToken)
            .ConfigureAwait(false);
        profile = profile with
        {
            WorkingDirectory = workingDirectory,
            NodeCount = CountProxyTargets(parsed),
        };
        await profileRepository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public Task ExportAsync(
        string configuration,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return AtomicFile.WriteAllTextAsync(
            Path.GetFullPath(destinationPath),
            configuration,
            cancellationToken);
    }

    private static int CountProxyTargets(JsonObject configuration)
    {
        var outboundCount = configuration["outbounds"] is JsonArray outbounds
            ? outbounds.OfType<JsonObject>().Count(static outbound =>
                outbound["type"]?.ToString() is not (
                    null or "direct" or "block" or "selector" or "urltest"))
            : 0;
        var endpointCount = configuration["endpoints"] is JsonArray endpoints
            ? endpoints.OfType<JsonObject>().Count()
            : 0;
        return outboundCount + endpointCount;
    }
}
