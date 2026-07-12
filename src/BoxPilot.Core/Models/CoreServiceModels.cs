namespace BoxPilot.Core.Models;

public static class CoreServiceErrorCodes
{
    public const string AuthorizationDenied = "boxpilot.service.authorization_denied";
    public const string InstallationFailed = "boxpilot.service.installation_failed";
    public const string RemovalFailed = "boxpilot.service.removal_failed";
    public const string Unavailable = "boxpilot.service.unavailable";
    public const string Disconnected = "boxpilot.service.disconnected";
}

public enum CoreServiceFailure
{
    AuthorizationDenied,
    InstallationFailed,
    RemovalFailed,
    Unavailable,
}

public sealed class CoreServiceException(
    CoreServiceFailure failure,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public CoreServiceFailure Failure { get; } = failure;
}
