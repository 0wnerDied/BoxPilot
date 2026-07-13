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
    ProfileRepository profileRepository,
    CustomRoutingService customRoutingService)
{
    private static readonly HashSet<string> ExplicitFormatSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "base64",
        "clash",
        "clash-meta",
        "clashmeta",
        "mihomo",
        "sing-box",
        "singbox",
        "v2ray",
    };

    private static readonly HashSet<string> ExplicitFormatExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".conf",
        ".json",
        ".txt",
        ".yaml",
        ".yml",
    };

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
        await ValidateSubscriptionAsync(
                configuration,
                "The converted subscription is not accepted by sing-box:",
                cancellationToken)
            .ConfigureAwait(false);

        var profile = await profileRepository.CreateAsync(
                name,
                configuration,
                ProfileSource.Subscription,
                cancellationToken)
            .ConfigureAwait(false);
        profile = ApplySubscriptionMetadata(profile, parsed, fetched) with
        {
            SubscriptionUrl = subscriptionUrl.AbsoluteUri,
        };
        await profileRepository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);

        return new ProfileImportOutcome(profile, parsed.Warnings, false);
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
            await profileRepository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
            return new ProfileImportOutcome(profile, [], true);
        }

        var parsed = download.Parsed
                     ?? throw new InvalidOperationException("The updated subscription returned no content.");
        var configuration = SerializeSubscription(parsed, subscriptionUrl);
        await ValidateSubscriptionAsync(
                configuration,
                "The updated subscription is not accepted by sing-box:",
                cancellationToken)
            .ConfigureAwait(false);
        configuration = await customRoutingService.ApplyAsync(
                profile.Id,
                configuration,
                cancellationToken)
            .ConfigureAwait(false);

        await profileRepository.WriteConfigurationAsync(profile, configuration, cancellationToken)
            .ConfigureAwait(false);
        var updated = ApplySubscriptionMetadata(profile, parsed, fetched);
        await profileRepository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        return new ProfileImportOutcome(updated, parsed.Warnings, false);
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
        if (!HasExplicitFormat(subscriptionUrl))
        {
            var refreshRepresentation = string.Equals(
                previousFormat,
                nameof(SubscriptionFormat.SingBoxJson),
                StringComparison.Ordinal);
            foreach (var clashUrl in CreateClashVariantUris(subscriptionUrl))
            {
                try
                {
                    var candidate = await FetchAndParseAsync(
                            clashUrl,
                            settings,
                            refreshRepresentation ? null : etag,
                            refreshRepresentation ? null : lastModified,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (candidate.Fetch.NotModified
                        && string.Equals(
                            previousFormat,
                            nameof(SubscriptionFormat.ClashYaml),
                            StringComparison.Ordinal))
                    {
                        return candidate;
                    }

                    if (candidate.Parsed is
                        {
                            Format: SubscriptionFormat.ClashYaml,
                            SourcePolicyGroupCount: > 0,
                        })
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (IsAlternativeRepresentationFailure(exception))
                {
                    // Providers are not required to expose every Clash representation.
                }
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

    private static IEnumerable<Uri> CreateClashVariantUris(Uri subscriptionUrl)
    {
        var pathBuilder = new UriBuilder(subscriptionUrl)
        {
            Path = subscriptionUrl.AbsolutePath.TrimEnd('/') + "/clash",
        };
        yield return pathBuilder.Uri;

        var queryBuilder = new UriBuilder(subscriptionUrl);
        var query = queryBuilder.Query.TrimStart('?');
        queryBuilder.Query = string.IsNullOrEmpty(query)
            ? "target=clash&format=clash"
            : $"{query}&target=clash&format=clash";
        yield return queryBuilder.Uri;
    }

    private static bool HasExplicitFormat(Uri subscriptionUrl)
    {
        var hasFormatQuery = subscriptionUrl.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static field => field.Split('=', 2)[0])
            .Any(static key => key.Equals("target", StringComparison.OrdinalIgnoreCase)
                               || key.Equals("format", StringComparison.OrdinalIgnoreCase));
        if (hasFormatQuery)
            return true;

        var path = subscriptionUrl.AbsolutePath.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        var segment = Uri.UnescapeDataString(path[(separator + 1)..]);
        return ExplicitFormatSegments.Contains(segment)
               || ExplicitFormatExtensions.Contains(Path.GetExtension(segment));
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

    private static Profile ApplySubscriptionMetadata(
        Profile profile,
        SubscriptionImportResult parsed,
        SubscriptionFetchResult fetched)
    {
        return profile with
        {
            SubscriptionFormat = parsed.Format.ToString(),
            ETag = fetched.ETag,
            LastModified = fetched.LastModified,
            NodeCount = parsed.NodeCount,
        };
    }

    private async Task ValidateSubscriptionAsync(
        string configuration,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        var validation = await configService.ValidateAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            throw new InvalidDataException(
                errorPrefix + Environment.NewLine + validation.CombinedOutput);
        }
    }
}
