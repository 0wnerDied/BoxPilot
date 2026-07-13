namespace BoxPilot.Core.Models;

public sealed record AppSettings
{
    public string SingBoxPath { get; init; } = string.Empty;

    public Guid? SelectedProfileId { get; init; }

    public string Theme { get; init; } = "Light";

    public string Language { get; init; } = "zh-CN";

    public bool StartCoreOnLaunch { get; init; }

    public bool CloseToTray { get; init; } = true;

    public bool EnableSystemProxy { get; init; } = true;

    public bool EnableTun { get; init; }

    public int MixedPort { get; init; } = 2080;

    public int ClashApiPort { get; init; } = 9090;

    public string ClashApiSecret { get; init; } = string.Empty;

    public int MaximumLogEntries { get; init; } = 2_000;

    public string SubscriptionUserAgent { get; init; } = "BoxPilot/0.1 sing-box";
}
