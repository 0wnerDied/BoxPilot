namespace BoxPilot.Core.Models;

public enum CoreState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

public enum CoreLogStream
{
    StandardOutput,
    StandardError,
    BoxPilot,
}

public sealed record CoreLogEntry(
    DateTimeOffset Timestamp,
    CoreLogStream Stream,
    string Message);

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }
            .Where(static value => value.Length > 0));
}

public sealed record CoreVersionInfo(
    string Version,
    string Platform,
    IReadOnlyList<string> Tags,
    string RawOutput);

public sealed record CoreStateChangedEventArgs(
    CoreState Previous,
    CoreState Current,
    int? ProcessId,
    string? Error);
