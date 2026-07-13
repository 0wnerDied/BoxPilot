using BoxPilot.Core.Infrastructure;

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

public enum CoreLogLevel
{
    System,
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

public sealed record CoreLogEntry(
    DateTimeOffset Timestamp,
    CoreLogStream Stream,
    CoreLogLevel Level,
    string RawMessage,
    AnsiTextDocument Content)
{
    public string Message => Content.Text;

    public string LevelLabel => Level switch
    {
        CoreLogLevel.System => "SYSTEM",
        CoreLogLevel.Trace => "TRACE",
        CoreLogLevel.Debug => "DEBUG",
        CoreLogLevel.Information => "INFO",
        CoreLogLevel.Warning => "WARN",
        CoreLogLevel.Error => "ERROR",
        CoreLogLevel.Fatal => "FATAL",
        _ => "LOG",
    };

    public bool IsSystem => Level == CoreLogLevel.System;

    public bool IsTrace => Level == CoreLogLevel.Trace;

    public bool IsDebug => Level == CoreLogLevel.Debug;

    public bool IsInformation => Level == CoreLogLevel.Information;

    public bool IsWarning => Level == CoreLogLevel.Warning;

    public bool IsError => Level == CoreLogLevel.Error;

    public bool IsFatal => Level == CoreLogLevel.Fatal;
}

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;

    public string CombinedTerminalOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }
            .Where(static value => value.Length > 0));

    public string CombinedOutput => AnsiTextParser.Parse(CombinedTerminalOutput).Text.Trim();
}

public sealed record CoreStateChangedEventArgs(
    CoreState Current,
    int? ProcessId,
    string? Error);

public sealed record ClashApiConnection(int Port, string Secret);
