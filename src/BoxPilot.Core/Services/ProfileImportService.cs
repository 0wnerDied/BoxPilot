using System.Security.Cryptography;
using System.Text;
using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;
using BoxPilot.Core.Subscriptions;

namespace BoxPilot.Core.Services;

public sealed class ProfileImportService(
    SubscriptionClient subscriptionClient,
    SubscriptionParser subscriptionParser,
    SingBoxConfigService configService,
    ProfileRepository profileRepository)
{
    public async Task<ProfileImportOutcome> ImportSubscriptionAsync(
        string name,
        Uri subscriptionUrl,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(subscriptionUrl);
        ArgumentNullException.ThrowIfNull(settings);

        var download = await FetchSubscriptionAsync(
                subscriptionUrl,
                settings,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var fetched = download.Fetch;
        var parsed = download.Parsed
                     ?? throw new InvalidOperationException("A new subscription returned no content.");
        var configuration = SerializeSubscription(parsed, subscriptionUrl);
        var validation = await configService.ValidateAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            throw new InvalidDataException(
                "The converted subscription is not accepted by sing-box:" + Environment.NewLine
                + validation.CombinedOutput);
        }

        var profile = await profileRepository.CreateAsync(
                name,
                configuration,
                ProfileSource.Subscription,
                cancellationToken)
            .ConfigureAwait(false);
        profile = profile with
        {
            SubscriptionUrl = subscriptionUrl.AbsoluteUri,
            SubscriptionFormat = parsed.Format.ToString(),
            ETag = fetched.ETag,
            LastModified = fetched.LastModified,
            LastSubscriptionUpdate = DateTimeOffset.UtcNow,
            UpdateIntervalHours = fetched.SuggestedUpdateHours
                                  ?? settings.DefaultSubscriptionUpdateHours,
            NodeCount = parsed.NodeCount,
            LastValidationMessage = validation.CombinedOutput,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await profileRepository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);

        return new ProfileImportOutcome(
            profile,
            parsed.Warnings,
            fetched.Quota,
            false,
            validation.CombinedOutput);
    }

    public async Task<ProfileImportOutcome> UpdateSubscriptionAsync(
        Profile profile,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(profile.SubscriptionUrl)
            || !Uri.TryCreate(profile.SubscriptionUrl, UriKind.Absolute, out var subscriptionUrl))
        {
            throw new InvalidOperationException("The profile has no valid subscription URL.");
        }

        var download = await FetchSubscriptionAsync(
                subscriptionUrl,
                settings,
                profile.ETag,
                profile.LastModified,
                profile.SubscriptionFormat,
                cancellationToken)
            .ConfigureAwait(false);
        var fetched = download.Fetch;
        if (fetched.NotModified)
        {
            var unchanged = profile with
            {
                LastSubscriptionUpdate = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await profileRepository.UpdateAsync(unchanged, cancellationToken).ConfigureAwait(false);
            return new ProfileImportOutcome(unchanged, [], fetched.Quota, true, "Not modified");
        }

        var parsed = download.Parsed
                     ?? throw new InvalidOperationException("The updated subscription returned no content.");
        var configuration = SerializeSubscription(parsed, subscriptionUrl);
        var validation = await configService.ValidateAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            throw new InvalidDataException(
                "The updated subscription is not accepted by sing-box:" + Environment.NewLine
                + validation.CombinedOutput);
        }

        await profileRepository.WriteConfigurationAsync(profile, configuration, cancellationToken)
            .ConfigureAwait(false);
        var updated = profile with
        {
            SubscriptionFormat = parsed.Format.ToString(),
            ETag = fetched.ETag,
            LastModified = fetched.LastModified,
            LastSubscriptionUpdate = DateTimeOffset.UtcNow,
            UpdateIntervalHours = fetched.SuggestedUpdateHours ?? profile.UpdateIntervalHours,
            NodeCount = parsed.NodeCount,
            LastValidationMessage = validation.CombinedOutput,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await profileRepository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        return new ProfileImportOutcome(
            updated,
            parsed.Warnings,
            fetched.Quota,
            false,
            validation.CombinedOutput);
    }

    public async Task<Profile> ImportConfigurationAsync(
        string name,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var formatted = configService.FormatJson(configuration);
        var validation = await configService.ValidateAsync(formatted, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
            throw new InvalidDataException(validation.CombinedOutput);

        var profile = await profileRepository.CreateAsync(
                name,
                formatted,
                ProfileSource.ImportedFile,
                cancellationToken)
            .ConfigureAwait(false);
        profile = profile with { LastValidationMessage = validation.CombinedOutput };
        await profileRepository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    private static SubscriptionBuildOptions CreateBuildOptions(AppSettings settings)
    {
        return new SubscriptionBuildOptions
        {
            MixedPort = settings.MixedPort,
            ClashApiPort = settings.ClashApiPort,
            ClashApiSecret = settings.ClashApiSecret,
            EnableSystemProxy = settings.EnableSystemProxy,
            EnableTun = settings.EnableTun,
        };
    }

    public static string CreateCacheId(Uri subscriptionUrl)
    {
        ArgumentNullException.ThrowIfNull(subscriptionUrl);
        var digest = SHA256.HashData(Utf8Text.Strict.GetBytes(subscriptionUrl.AbsoluteUri));
        return "subscription-" + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal async Task<(
        SubscriptionFetchResult Fetch,
        SubscriptionImportResult? Parsed)> FetchSubscriptionAsync(
        Uri subscriptionUrl,
        AppSettings settings,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        string? previousFormat = null,
        CancellationToken cancellationToken = default)
    {
        var clashUrl = CreateClashVariantUri(subscriptionUrl);
        if (clashUrl != subscriptionUrl)
        {
            var refreshRepresentation = string.Equals(
                previousFormat,
                nameof(SubscriptionFormat.SingBoxJson),
                StringComparison.Ordinal);
            try
            {
                return await FetchAndParseAsync(
                        clashUrl,
                        settings,
                        refreshRepresentation ? null : etag,
                        refreshRepresentation ? null : lastModified,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsAlternativeRepresentationFailure(exception))
            {
                // Providers are not required to understand Clash format hints.
            }
        }

        return await FetchAndParseAsync(
                subscriptionUrl,
                settings,
                etag,
                lastModified,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(
        SubscriptionFetchResult Fetch,
        SubscriptionImportResult? Parsed)> FetchAndParseAsync(
        Uri subscriptionUrl,
        AppSettings settings,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken cancellationToken)
    {
        var fetched = await subscriptionClient.FetchAsync(
                subscriptionUrl,
                settings.SubscriptionUserAgent,
                etag,
                lastModified,
                cancellationToken)
            .ConfigureAwait(false);
        var parsed = fetched.NotModified
            ? null
            : subscriptionParser.Parse(fetched.Content, CreateBuildOptions(settings));
        return (fetched, parsed);
    }

    private static Uri CreateClashVariantUri(Uri subscriptionUrl)
    {
        var builder = new UriBuilder(subscriptionUrl);
        var query = builder.Query.TrimStart('?');
        var hasExplicitFormat = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static field => field.Split('=', 2)[0])
            .Any(static key => key.Equals("target", StringComparison.OrdinalIgnoreCase)
                               || key.Equals("format", StringComparison.OrdinalIgnoreCase));
        if (hasExplicitFormat)
            return subscriptionUrl;

        builder.Query = string.IsNullOrEmpty(query)
            ? "target=clash&format=clash"
            : $"{query}&target=clash&format=clash";
        return builder.Uri;
    }

    private static bool IsAlternativeRepresentationFailure(Exception exception)
    {
        return exception is HttpRequestException or InvalidDataException or DecoderFallbackException;
    }

    private string SerializeSubscription(SubscriptionImportResult parsed, Uri subscriptionUrl)
    {
        var configuration = parsed.Format == SubscriptionFormat.SingBoxJson
            ? parsed.Configuration
            : configService.PrepareManagedSubscription(
                parsed.Configuration,
                CreateCacheId(subscriptionUrl),
                parsed.SourcePolicyGroupCount > 0);
        return configService.Serialize(configuration);
    }
}
