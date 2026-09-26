namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Parses and validates <c>AutoFix:VerificationCommand</c> ("executable arg1 arg2 …").
/// The executable and arguments are passed to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// verbatim — no shell — so metacharacters that would be meaningful to a shell are
/// rejected at startup instead of being reinterpreted.
/// </summary>
public static class ProcessFixCommand
{
    /// <summary>Characters that would carry meaning under a shell and are therefore refused.</summary>
    private static readonly char[] Metacharacters = ['|', '&', ';', '<', '>', '`', '$', '(', ')', '{', '}', '\'', '"', '\\', '*', '?', '[', ']', '~', '#', '!'];

    /// <summary>Splits the command into executable + arguments; throws on empty input or metacharacters.</summary>
    public static (string Executable, string[] Arguments) Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("verification command must not be empty", nameof(command));
        }

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.IndexOfAny(Metacharacters) >= 0)
            {
                throw new ArgumentException(
                    $"verification command token '{token}' contains a shell metacharacter; " +
                    "the command runs without a shell — pass a plain executable plus arguments",
                    nameof(command));
            }
        }

        return (tokens[0], tokens[1..]);
    }
}
