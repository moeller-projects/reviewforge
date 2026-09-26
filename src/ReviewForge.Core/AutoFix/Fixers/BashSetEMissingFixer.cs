using System.Text.RegularExpressions;

namespace ReviewForge.Core.AutoFix.Fixers;

/// <summary>
/// Fixes bash.set-e-missing: when the anchor is line 1 of a script starting with a
/// shebang and no line matches <c>^\s*set\s+-[a-zA-Z]*e</c>, appends
/// <c>set -euo pipefail</c> directly after the shebang. Declines otherwise (a set line
/// already exists, no shebang, or the anchor is not line 1).
/// </summary>
public sealed partial class BashSetEMissingFixer : IFindingFixer
{
    public string RuleId => "bash.set-e-missing";

    public FixProposal? TryPropose(FixContext context)
    {
        var anchor = context.Finding.Anchor;
        if (anchor is null || anchor.StartLine != 1 || context.FileLines.Length == 0)
        {
            return null;
        }

        var line1 = context.FileLines[0];
        if (!IsBashShebang(line1))
        {
            return null;
        }

        if (context.FileLines.Any(l => SetLine().IsMatch(l)))
        {
            return null; // some `set -...e...` form already exists (e.g. `set -ex`)
        }

        return new FixProposal(
            context.FilePath,
            1,
            1,
            line1 + "\nset -euo pipefail",
            "adds `set -euo pipefail` so the script fails fast on errors, unset variables, and pipe failures");
    }

    private static bool IsBashShebang(string line)
    {
        if (!line.StartsWith("#!", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = line[2..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var interpreter = Path.GetFileName(parts[0]);
        if (string.Equals(interpreter, "bash", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(interpreter, "env", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && parts[i][0] == '-')
            {
                continue;
            }

            return string.Equals(Path.GetFileName(parts[i]), "bash", StringComparison.Ordinal);
        }

        return false;
    }

    [GeneratedRegex(@"^\s*set\s+-[a-zA-Z]*e")]
    private static partial Regex SetLine();
}
