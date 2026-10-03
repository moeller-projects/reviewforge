using ReviewForge.Core.Ports;

namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Suppresses ReviewForge's own automation loop: a CommitOnHead push creates a new PR head,
/// and without this guard discovery would review the bot's commit, which could push again.
/// Suppression requires ALL conjuncts: an exact <c>ReviewForge-Run:</c> trailer line in the
/// commit message's trailer block whose value parses as a GUID, AND the commit's author email
/// equal to the configured bot identity. Manual triggers bypass the guard entirely (a human
/// asking for a review of the bot's commit is desirable) — only discovery-triggered runs are
/// suppressed, so the worst case of a forged trailer is a skipped AUTOMATIC review, never
/// content manipulation.
/// </summary>
public static class LoopGuard
{
    /// <summary>True when the head commit is verifiably bot-authored per the three-conjunct
    /// rule. A null <see cref="AutoFixOptions.CommitAuthorEmail"/> (Suggestion mode) can never
    /// match — the guard is inert unless CommitOnHead is configured.</summary>
    public static bool IsBotAuthoredHead(TipCommitInfo info, AutoFixOptions options)
        => options.IsCommitOnHead
           && !string.IsNullOrWhiteSpace(options.CommitAuthorEmail)
           && TryParseRunTrailer(info.Message, out _)
           && string.Equals(info.AuthorEmail, options.CommitAuthorEmail, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact-trailer parse: a <c>ReviewForge-Run: {guid}</c> line inside the commit
    /// message's trailer block (the trailing run of <c>Name: value</c> lines). A mention in
    /// prose — subject or body text — does NOT count, and neither does a malformed GUID.</summary>
    public static bool TryParseRunTrailer(string message, out Guid runId)
    {
        runId = default;
        var lines = message.Replace("\r\n", "\n").Split('\n');

        // Commit messages conventionally end with a newline; ignore terminal blank lines
        // before walking the contiguous trailer block.
        var lastLine = lines.Length - 1;
        while (lastLine >= 0 && string.IsNullOrWhiteSpace(lines[lastLine]))
        {
            lastLine--;
        }

        var found = false;
        for (var i = lastLine; i >= 0; i--)
        {
            var line = lines[i];
            if (!IsTrailerLine(line))
            {
                break;
            }

            if (line.StartsWith(ConventionalCommitBuilder.RunTrailerName + ":", StringComparison.Ordinal)
                && Guid.TryParse(line[(ConventionalCommitBuilder.RunTrailerName.Length + 1)..].Trim(), out var parsed))
            {
                runId = parsed;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Git trailer grammar: "Token: value" where the token is alphanumeric + dashes.</summary>
    private static bool IsTrailerLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || colon + 1 >= line.Length || line[colon + 1] != ' ')
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            if (!char.IsLetterOrDigit(line[i]) && line[i] != '-')
            {
                return false;
            }
        }

        return true;
    }
}
