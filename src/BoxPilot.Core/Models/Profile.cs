using System.Text.Json.Serialization;

namespace BoxPilot.Core.Models;

public enum ProfileSource
{
    Manual,
    Subscription,
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

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTimeOffset UpdatedAtLocal => UpdatedAt.ToLocalTime();

    public DateTimeOffset? LastSubscriptionUpdate { get; init; }

    public int UpdateIntervalHours { get; init; } = 24;

    public int NodeCount { get; init; }

    public string? LastValidationMessage { get; init; }
}

internal sealed record ProfileIndex
{
    public int SchemaVersion { get; init; } = 1;

    public List<Profile> Profiles { get; init; } = [];
}
