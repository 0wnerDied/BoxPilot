using System.Text.Json;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class ProfileRepository(AppPaths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            return index.Profiles
                .OrderByDescending(static profile => profile.UpdatedAt)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Profile?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profiles = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile => profile.Id == id);
    }

    public async Task<Profile> CreateAsync(
        string name,
        string configuration,
        ProfileSource source = ProfileSource.Manual,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid();
            var profile = new Profile
            {
                Id = id,
                Name = name.Trim(),
                ConfigFileName = $"{id:N}.json",
                Source = source,
            };

            await AtomicFile.WriteAllTextAsync(
                    paths.GetProfileConfigPath(profile),
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);

            index.Profiles.Add(profile);
            await SaveIndexAsync(index, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var position = index.Profiles.FindIndex(item => item.Id == profile.Id);
            if (position < 0)
                throw new KeyNotFoundException($"Profile {profile.Id} does not exist.");

            index.Profiles[position] = profile with { UpdatedAt = DateTimeOffset.UtcNow };
            await SaveIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var profile = index.Profiles.FirstOrDefault(item => item.Id == id);
            if (profile is null)
                return;

            index.Profiles.Remove(profile);
            var configPath = paths.GetProfileConfigPath(profile);
            if (File.Exists(configPath))
                File.Delete(configPath);

            await SaveIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> ReadConfigurationAsync(
        Profile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return await File.ReadAllTextAsync(paths.GetProfileConfigPath(profile), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task WriteConfigurationAsync(
        Profile profile,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configuration);
        return AtomicFile.WriteAllTextAsync(
            paths.GetProfileConfigPath(profile),
            configuration,
            cancellationToken);
    }

    public string GetConfigurationPath(Profile profile)
    {
        return paths.GetProfileConfigPath(profile);
    }

    private async Task<ProfileIndex> LoadIndexAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.ProfileIndexFile))
            return new ProfileIndex();

        try
        {
            await using var stream = File.OpenRead(paths.ProfileIndexFile);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    BoxPilotJsonContext.Default.ProfileIndex,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new ProfileIndex();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The BoxPilot profile index is not valid JSON.", exception);
        }
    }

    private Task SaveIndexAsync(ProfileIndex index, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(index, BoxPilotJsonContext.Default.ProfileIndex);
        return AtomicFile.WriteAllTextAsync(paths.ProfileIndexFile, json, cancellationToken);
    }
}
