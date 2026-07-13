using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
        ProfilesDirectory = Path.Combine(RootDirectory, "profiles");
        RuntimeDirectory = Path.Combine(RootDirectory, "runtime");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        RuleSetsDirectory = Path.Combine(RootDirectory, "rule-sets");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        ProfileIndexFile = Path.Combine(RootDirectory, "profiles.json");
        CustomRoutingFile = Path.Combine(RootDirectory, "custom-routing.json");
    }

    public string RootDirectory { get; }

    public string ProfilesDirectory { get; }

    public string RuntimeDirectory { get; }

    public string CacheDirectory { get; }

    public string RuleSetsDirectory { get; }

    public string SettingsFile { get; }

    public string ProfileIndexFile { get; }

    public string CustomRoutingFile { get; }

    public static AppPaths CreateDefault()
    {
        var baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".boxpilot");

        return new AppPaths(Path.Combine(baseDirectory, "BoxPilot"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(RuleSetsDirectory);
    }

    public string GetProfileConfigPath(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var fileName = Path.GetFileName(profile.ConfigFileName);
        if (!string.Equals(fileName, profile.ConfigFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Profile configuration file name is invalid.");

        return Path.Combine(ProfilesDirectory, fileName);
    }

    public string GetRuleSetDirectory(Guid profileId)
    {
        return Path.Combine(RuleSetsDirectory, profileId.ToString("N"));
    }
}
