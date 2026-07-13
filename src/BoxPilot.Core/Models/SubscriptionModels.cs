using System.Text.Json.Nodes;

namespace BoxPilot.Core.Models;

public enum SubscriptionFormat
{
    SingBoxJson,
    ClashYaml,
    Base64UriList,
    UriList,
}

internal sealed record SubscriptionFetchResult(
    string Content,
    string? ETag,
    DateTimeOffset? LastModified,
    bool NotModified = false);

internal sealed record SubscriptionBuildOptions
{
    public int MixedPort { get; init; } = 2080;

    public int ClashApiPort { get; init; } = 9090;

    public string ClashApiSecret { get; init; } = string.Empty;

    public bool EnableSystemProxy { get; init; } = true;

    public bool EnableTun { get; init; }
}

internal sealed record SubscriptionImportResult(
    SubscriptionFormat Format,
    JsonObject Configuration,
    int NodeCount,
    IReadOnlyList<string> Warnings,
    int SourcePolicyGroupCount = 0);

public sealed record ProxyNode(
    string Name,
    string Type,
    int? Delay,
    bool SupportsUdp,
    bool IsGroup);

public sealed record ProxyChoice(
    string Group,
    bool IsSelectable,
    string Selected,
    IReadOnlyList<ProxyNode> Options);

public sealed record TrafficSnapshot(long UploadBytesPerSecond, long DownloadBytesPerSecond);

public sealed record TrafficTotals(long UploadBytes, long DownloadBytes);

public sealed record ProfileImportOutcome(
    Profile Profile,
    IReadOnlyList<string> Warnings,
    bool NotModified);
