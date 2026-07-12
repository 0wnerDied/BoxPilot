using System.Security.Cryptography;
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

        var fetched = await subscriptionClient.FetchAsync(
                subscriptionUrl,
                settings.SubscriptionUserAgent,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var parsed = subscriptionParser.Parse(fetched.Content, CreateBuildOptions(settings));
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

        var fetched = await subscriptionClient.FetchAsync(
                subscriptionUrl,
                settings.SubscriptionUserAgent,
                profile.ETag,
                profile.LastModified,
                cancellationToken)
            .ConfigureAwait(false);
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

        var parsed = subscriptionParser.Parse(fetched.Content, CreateBuildOptions(settings));
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

    private string SerializeSubscription(SubscriptionImportResult parsed, Uri subscriptionUrl)
    {
        var configuration = parsed.Format == SubscriptionFormat.SingBoxJson
            ? parsed.Configuration
            : configService.PrepareManagedSubscription(
                parsed.Configuration,
                CreateCacheId(subscriptionUrl));
        return configService.Serialize(configuration);
    }
}
