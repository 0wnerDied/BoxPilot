using System.Text;

namespace BoxPilot.Core.Infrastructure;

public static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var arguments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var character in commandLine)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                if (quote == character)
                    quote = null;
                else if (quote is null)
                    quote = character;
                else
                    current.Append(character);

                continue;
            }

            if (char.IsWhiteSpace(character) && quote is null)
            {
                AddArgument(arguments, current);
                continue;
            }

            current.Append(character);
        }

        if (escaped)
            current.Append('\\');
        if (quote is not null)
            throw new FormatException("The command line contains an unterminated quote.");

        AddArgument(arguments, current);
        return arguments;
    }

    private static void AddArgument(List<string> arguments, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        arguments.Add(current.ToString());
        current.Clear();
    }
}
