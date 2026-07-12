using System.Security.Cryptography;
using System.Text.Json;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Services;

public sealed class SettingsStore(AppPaths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings settings;
            if (!File.Exists(paths.SettingsFile))
            {
                settings = CreateDefault();
                await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
                return settings;
            }

            await using var stream = File.OpenRead(paths.SettingsFile);
            settings = await JsonSerializer.DeserializeAsync(
                    stream,
                    JsonDefaults.Context.AppSettings,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? CreateDefault();

            if (string.IsNullOrWhiteSpace(settings.ClashApiSecret))
            {
                settings = settings with { ClashApiSecret = CreateSecret() };
                await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("BoxPilot settings are not valid JSON.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static AppSettings CreateDefault()
    {
        return new AppSettings { ClashApiSecret = CreateSecret() };
    }

    private static string CreateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    private Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(settings, JsonDefaults.Context.AppSettings);
        return AtomicFile.WriteAllTextAsync(paths.SettingsFile, json, cancellationToken);
    }
}
