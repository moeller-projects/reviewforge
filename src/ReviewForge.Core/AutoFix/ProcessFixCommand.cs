using System.Text;

namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Parses and validates <c>AutoFix:VerificationCommand</c> ("executable arg1 arg2 …").
/// The executable and arguments are passed to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// verbatim — no shell — so metacharacters that would be meaningful to a shell are
/// rejected at startup instead of being reinterpreted.
/// </summary>
public static class ProcessFixCommand
{
    public const string FilePlaceholder = "{file}";
    private static readonly char[] Metacharacters = ['|', '&', ';', '<', '>', '`', '$', '(', ')', '*', '?', '[', ']', '~', '#', '!'];

    public readonly record struct Parsed(string Executable, string[] Arguments)
    {
        public bool HasFilePlaceholder => Arguments.Any(a => a == FilePlaceholder);
        public void Deconstruct(out string executable, out string[] arguments)
            => (executable, arguments) = (Executable, Arguments);
    }

    /// <summary>Splits the command into executable + arguments without invoking a shell.</summary>
    public static Parsed Parse(string command)
    {
        if (!TryParse(command, out var parsed, out var error))
        {
            throw new ArgumentException(error, nameof(command));
        }

        return parsed;
    }

    public static bool TryParse(string? command, out Parsed parsed, out string error)
    {
        parsed = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "verification command must not be empty";
            return false;
        }

        if (command.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            error = "verification command must not contain carriage returns, newlines, or tabs";
            return false;
        }

        var tokens = new List<string>();
        var token = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;
        foreach (var character in command)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
            }
            else if (character == ' ' && !inQuotes)
            {
                if (tokenStarted)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                token.Append(character);
                tokenStarted = true;
            }
        }

        if (inQuotes)
        {
            error = "verification command contains an unbalanced double quote";
            return false;
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        foreach (var parsedToken in tokens)
        {
            if (parsedToken.Contains(FilePlaceholder, StringComparison.Ordinal)
                && !string.Equals(parsedToken, FilePlaceholder, StringComparison.Ordinal))
            {
                error = $"verification command token '{parsedToken}' must use {FilePlaceholder} as a whole argument";
                return false;
            }

            if (parsedToken != FilePlaceholder && parsedToken.IndexOfAny(Metacharacters) >= 0
                || parsedToken == FilePlaceholder && parsedToken.IndexOfAny(Metacharacters) >= 0)
            {
                error = $"verification command token '{parsedToken}' contains a shell metacharacter; " +
                        "the command runs without a shell — pass a plain executable plus arguments";
                return false;
            }
        }

        if (tokens.Count == 0)
        {
            error = "verification command must not be empty";
            return false;
        }

        parsed = new Parsed(tokens[0], tokens.Skip(1).ToArray());
        return true;
    }
}
