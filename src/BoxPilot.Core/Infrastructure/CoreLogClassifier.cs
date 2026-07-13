using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal static class CoreLogClassifier
{
    public static CoreLogLevel Detect(CoreLogStream stream, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (stream == CoreLogStream.BoxPilot)
            return CoreLogLevel.System;

        var value = message.AsSpan();
        for (var index = 0; index < value.Length;)
        {
            while (index < value.Length && !char.IsAsciiLetter(value[index]))
                index++;
            var start = index;
            while (index < value.Length && char.IsAsciiLetter(value[index]))
                index++;
            var token = value[start..index];
            if (token.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
                return CoreLogLevel.Trace;
            if (token.Equals("DEBUG", StringComparison.OrdinalIgnoreCase))
                return CoreLogLevel.Debug;
            if (token.Equals("INFO", StringComparison.OrdinalIgnoreCase))
                return CoreLogLevel.Information;
            if (token.Equals("WARN", StringComparison.OrdinalIgnoreCase)
                || token.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                return CoreLogLevel.Warning;
            }
            if (token.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                return CoreLogLevel.Error;
            if (token.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                return CoreLogLevel.Fatal;
        }

        return stream == CoreLogStream.StandardError
            ? CoreLogLevel.Error
            : CoreLogLevel.Information;
    }
}
