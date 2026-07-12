using System.Text.Json.Nodes;

namespace BoxPilot.Core.Models;

public enum SubscriptionFormat
{
    SingBoxJson,
    ClashYaml,
    Base64UriList,
    UriList,
}

public sealed record SubscriptionQuota(
    long? UploadBytes,
    long? DownloadBytes,
    long? TotalBytes,
    DateTimeOffset? ExpiresAt);

public sealed record SubscriptionFetchResult(
    string Content,
    string? MediaType,
    string? ETag,
    DateTimeOffset? LastModified,
    int? SuggestedUpdateHours,
    SubscriptionQuota? Quota,
    bool NotModified = false);

public sealed record SubscriptionBuildOptions
{
    public int MixedPort { get; init; } = 2080;

    public int ClashApiPort { get; init; } = 9090;

    public string ClashApiSecret { get; init; } = string.Empty;

    public bool EnableSystemProxy { get; init; } = true;

    public bool EnableTun { get; init; }
}

public sealed record SubscriptionImportResult(
    SubscriptionFormat Format,
    JsonObject Configuration,
    int NodeCount,
    IReadOnlyList<string> Warnings);

public sealed record ProxyChoice(string Group, string Selected, IReadOnlyList<string> Options);

public sealed record TrafficSnapshot(long UploadBytesPerSecond, long DownloadBytesPerSecond);

public sealed record ProfileImportOutcome(
    Profile Profile,
    IReadOnlyList<string> Warnings,
    SubscriptionQuota? Quota,
    bool NotModified,
    string ValidationOutput);
