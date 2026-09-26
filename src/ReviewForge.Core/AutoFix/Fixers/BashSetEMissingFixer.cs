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
        if (!line1.StartsWith("#!", StringComparison.Ordinal))
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

    [GeneratedRegex(@"^\s*set\s+-[a-zA-Z]*e")]
    private static partial Regex SetLine();
}
