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
    /// <summary>Characters that would carry meaning under a shell and are therefore refused.
    /// Directory separators are deliberately absent: tokens are passed to
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> verbatim (no shell),
    /// so Windows paths like <c>C:\tools\verify.exe</c> must parse.</summary>
    private static readonly char[] Metacharacters = ['|', '&', ';', '<', '>', '`', '$', '(', ')', '{', '}', '*', '?', '[', ']', '~', '#', '!'];

    /// <summary>Splits the command into executable + arguments; throws on empty input, control
    /// characters, unbalanced double quotes, or metacharacters.</summary>
    public static (string Executable, string[] Arguments) Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("verification command must not be empty", nameof(command));
        }

        if (command.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new ArgumentException(
                "verification command must not contain carriage returns, newlines, or tabs",
                nameof(command));
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
            throw new ArgumentException(
                "verification command contains an unbalanced double quote",
                nameof(command));
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        foreach (var parsedToken in tokens)
        {
            if (parsedToken.IndexOfAny(Metacharacters) >= 0)
            {
                throw new ArgumentException(
                    $"verification command token '{parsedToken}' contains a shell metacharacter; " +
                    "the command runs without a shell — pass a plain executable plus arguments",
                    nameof(command));
            }
        }

        return (tokens[0], tokens.Skip(1).ToArray());
    }
}
