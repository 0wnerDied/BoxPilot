using System.Text.Json.Serialization;

namespace BoxPilot.Core.Models;

public enum RuleSetFormat
{
    [JsonStringEnumMemberName("source")]
    Source,

    [JsonStringEnumMemberName("binary")]
    Binary,
}

public enum RuleSetSource
{
    [JsonStringEnumMemberName("local")]
    Local,

    [JsonStringEnumMemberName("remote")]
    Remote,
}

public sealed record CustomRuleSet
{
    public required Guid Id { get; init; }

    public required Guid ProfileId { get; init; }

    public required string Name { get; init; }

    public string? FileName { get; init; }

    public string? Url { get; init; }

    public string UpdateInterval { get; init; } = "1d";

    public required string Outbound { get; init; }

    public required RuleSetFormat Format { get; init; }

    public RuleSetSource Source { get; init; }

    [JsonIgnore]
    public string Tag => $"boxpilot-custom-{Id:N}";
}

public sealed record RoutingOutbound(string Tag);

public sealed record CustomRuleSetChange(CustomRuleSet RuleSet, string Configuration);

internal sealed record CustomRoutingIndex
{
    public List<CustomRuleSet> RuleSets { get; init; } = [];
}
