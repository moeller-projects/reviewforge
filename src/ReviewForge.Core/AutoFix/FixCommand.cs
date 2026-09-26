using ReviewForge.Core.Domain;

namespace ReviewForge.Core.AutoFix;

/// <summary>An author-issued fix command on a PR thread.</summary>
public sealed record FixCommand(
    int ThreadId,
    ThreadAnchor Anchor,          // the thread's file/line; required
    string? Instruction,          // free text after "/rf fix", may be null
    string QuotedComment);        // the comment being answered (bounded)

/// <summary>
/// Detects "/rf fix" commands from the PR author. Eligible commands: ACTIVE threads whose
/// LAST comment is by the PR author id, starts (case-insensitive, trimmed) with "/rf fix"
/// followed by end-of-string or whitespace, was published after the watermark, and whose
/// thread has a file anchor. Fixed/Closed threads and threads whose last comment is ours are
/// excluded (the latter makes replies self-idempotent). Commands from non-authors are
/// ignored — never publicly denied.
/// </summary>
public static class FixCommandDetector
{
    public const string Prefix = "/rf fix";

    /// <summary>Bound for the command instruction carried into the fix prompt.</summary>
    public const int MaxInstructionChars = 300;

    /// <summary>Bound for the quoted comment carried into the fix prompt.</summary>
    public const int MaxQuotedCommentChars = 1000;

    public static IReadOnlyList<FixCommand> Scan(
        IReadOnlyList<ReviewThread> threads,
        string prCreatorId,
        DateTimeOffset? watermark)
    {
        List<FixCommand>? commands = null;
        foreach (var thread in threads)
        {
            if (thread.Status != ReviewThreadStatus.Active || thread.Anchor is null)
            {
                continue;
            }

            var last = thread.LastComment;
            if (last is null || last.IsBot)
            {
                continue; // our own reply makes the bot last → cannot retrigger (self-idempotent)
            }

            if (!IsAuthor(last, prCreatorId))
            {
                continue; // only the PR author can command fixes; non-author commands are ignored
            }

            if (watermark is { } watermarkTime && last.PublishedAt <= watermarkTime)
            {
                continue; // older than the last completed run's observation — already handled
            }

            var text = last.Text.Trim();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || (text.Length > Prefix.Length && !char.IsWhiteSpace(text[Prefix.Length])))
            {
                continue;
            }

            var instruction = text.Length > Prefix.Length ? text[Prefix.Length..].Trim() : null;
            if (instruction?.Length == 0)
            {
                instruction = null;
            }
            else if (instruction is not null && instruction.Length > MaxInstructionChars)
            {
                instruction = instruction[..MaxInstructionChars];
            }

            (commands ??= []).Add(new FixCommand(
                thread.Id,
                thread.Anchor,
                instruction,
                Quote(thread)));
        }

        return commands ?? [];
    }

    private static bool IsAuthor(ThreadComment comment, string creatorId)
        => creatorId.Length > 0
           && string.Equals(comment.AuthorId, creatorId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The comment being answered: the previous non-command HUMAN comment. Bot
    /// comments (our own findings, our own replies) are skipped — agent-authored text is
    /// never re-ingested as prompt data. Empty when there is no human comment to quote.</summary>
    private static string Quote(ReviewThread thread)
    {
        ThreadComment? quoted = null;
        for (var i = thread.Comments.Count - 1; i >= 0; i--)
        {
            var c = thread.Comments[i];
            var text = c.Text.Trim();
            if (c.IsBot || text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            quoted = c;
            break;
        }

        var body = quoted?.Text ?? string.Empty;
        return body.Length <= MaxQuotedCommentChars ? body : body[..MaxQuotedCommentChars] + "…";
    }
}
