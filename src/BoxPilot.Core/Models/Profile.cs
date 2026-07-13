using System.Text.Json.Serialization;

namespace BoxPilot.Core.Models;

public enum ProfileSource
{
    [JsonStringEnumMemberName("manual")]
    Manual,

    [JsonStringEnumMemberName("subscription")]
    Subscription,

    [JsonStringEnumMemberName("importedFile")]
    ImportedFile,
}

public sealed record Profile
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ConfigFileName { get; init; }

    public ProfileSource Source { get; init; }

    public string? SubscriptionUrl { get; init; }

    public string? SubscriptionFormat { get; init; }

    public string? WorkingDirectory { get; init; }

    public bool? ManageStandardRoutingModes { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTimeOffset UpdatedAtLocal => UpdatedAt.ToLocalTime();

    public int NodeCount { get; init; }
}

internal sealed record ProfileIndex
{
    public List<Profile> Profiles { get; init; } = [];
}
