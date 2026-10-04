using System.Text;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Pure builder for the conventional-commit messages stage 7.7 pushes in CommitOnHead mode.
/// No IO, no clock, no randomness — the same inputs always produce the same message.
/// Layout: <c>type(scope): description</c> subject (≤72 chars), a blank line, one bullet per
/// fix (wrapped at 72), a blank line, then the trailer block: <c>ReviewForge-Run: {guid}</c>
/// plus one <c>ReviewForge-Thread: {id}</c> per commanded fix. The run trailer is the loop
/// guard's suppression signal (<see cref="LoopGuard.TryParseRunTrailer"/>).
/// </summary>
public static class ConventionalCommitBuilder
{
    public const string RunTrailerName = "ReviewForge-Run";
    public const string ThreadTrailerName = "ReviewForge-Thread";
    /// <summary>Trailer marking a commit that contains AI-drafted changes (LlmCommanded fix
    /// passes and resolve resolutions are always agent-drafted). Required labeling for any
    /// AI-authored change the service publishes.</summary>
    public const string AiDraftedTrailerName = "ReviewForge-AI-Drafted";

    /// <summary>One fix's contribution to a commit: the applied fix plus its originating
    /// finding (null for commanded thread fixes, which have no finding).</summary>
    public sealed record CommitFixInput(AppliedFix Fix, RichFinding? Finding);

    /// <summary>Hard subject-length cap (conventional-commit tooling and ADO render width).</summary>
    public const int MaxSubjectLength = 72;

    /// <summary>Body wrap width.</summary>
    public const int BodyWrapWidth = 72;

    public static string Build(Guid runId, IReadOnlyList<CommitFixInput> fixes)
    {
        if (fixes.Count == 0)
        {
            throw new ArgumentException("a commit needs at least one fix", nameof(fixes));
        }

        var subject = Subject(fixes);
        var sb = new StringBuilder();
        sb.Append(subject).Append('\n');
        sb.Append('\n');
        foreach (var input in fixes)
        {
            var bullet = $"- `{OneLine(input.Fix.Proposal.FilePath)}:{input.Fix.Proposal.StartLine}–{input.Fix.Proposal.EndLine}` — {OneLine(input.Fix.Proposal.Rationale)}";
            AppendWrapped(sb, bullet);
        }

        sb.Append('\n');
        sb.Append(RunTrailerName).Append(": ").Append(runId.ToString("D")).Append('\n');
        foreach (var threadId in fixes
                     .Select(f => f.Fix.Proposal.SourceThreadId)
                     .Where(id => id is not null)
                     .Distinct()
                     .OrderBy(id => id!.Value))
        {
            sb.Append(ThreadTrailerName).Append(": ").Append(threadId!.Value).Append('\n');
        }

        if (fixes.Any(f => f.Fix.Proposal.Origin == FixOrigin.LlmCommanded))
        {
            sb.Append(AiDraftedTrailerName).Append(": true\n");
        }

        return sb.ToString();
    }

    public static string BuildResolve(
        Guid runId, int prId, int threadId, string category, string filePath, string rationale, string evidence,
        IReadOnlyList<int>? relatedThreadIds = null)
    {
        var type = category.ToLowerInvariant() switch
        {
            "performance" => "perf",
            "docs" => "docs",
            "style" => "style",
            "test" => "test",
            _ => "fix",
        };
        var segments = filePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var scopeText = segments.Length > 1 ? segments[0] : Path.GetFileNameWithoutExtension(segments[0]);
        var scope = new string(scopeText.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray()).Trim('-', '.');
        var prefix = scope.Length == 0 ? $"{type}: " : $"{type}({scope}): ";
        var subject = prefix + OneLine(rationale);
        if (subject.Length > MaxSubjectLength) subject = subject[..MaxSubjectLength].TrimEnd();
        var sb = new StringBuilder(subject).Append("\n\n");
        AppendWrapped(sb, $"- resolve thread #{threadId} in `{OneLine(filePath)}` — {OneLine(rationale)}");
        if (!string.IsNullOrWhiteSpace(evidence)) AppendWrapped(sb, $"- Evidence: {OneLine(evidence)}");
        var ids = (relatedThreadIds ?? [threadId]).Distinct().Order().ToArray();
        foreach (var id in ids) AppendWrapped(sb, $"- References review thread #{id}");
        sb.Append('\n');
        foreach (var id in ids) sb.Append("Refs: !").Append(prId).Append(" thread ").Append(id).Append('\n');
        sb.Append(RunTrailerName).Append(": ").Append(runId.ToString("D")).Append('\n');
        foreach (var id in ids) sb.Append(ThreadTrailerName).Append(": ").Append(id).Append('\n');
        // Resolve resolutions are always agent-drafted — the AI label is unconditional.
        sb.Append(AiDraftedTrailerName).Append(": true\n");
        return sb.ToString();
    }

    /// <summary>The subject line of a message produced by <see cref="Build"/> (first line).</summary>
    public static string SubjectOf(string message)
        => message.Split('\n', 2)[0].TrimEnd('\r');

    private static string Subject(IReadOnlyList<CommitFixInput> fixes)
    {
        var type = CommitType(fixes);
        var scope = Scope(fixes);
        var description = Description(fixes);
        var prefix = scope is null ? $"{type}: " : $"{type}({scope}): ";
        var subject = prefix + description;
        return subject.Length <= MaxSubjectLength ? subject : subject[..MaxSubjectLength];
    }

    /// <summary>Mixed sets: severity ranks first; ties prefer bug/security, then performance,
    /// style, and docs. A security fix must never ship under a "style" type. Commanded fixes
    /// (no finding) count as "fix".</summary>
    private static string CommitType(IReadOnlyList<CommitFixInput> fixes)
    {
        var decisive = fixes
            .Where(f => f.Finding is not null)
            .OrderBy(f => SeverityRank(f.Finding!.Severity))
            .ThenBy(f => CategoryRank(f.Finding!.Category))
            .FirstOrDefault();
        var category = decisive?.Finding!.Category;
        return category switch
        {
            "performance" => "perf",
            "style" => "style",
            "docs" => "docs",
            _ => "fix", // bug, security, null (commanded), unknown
        };
    }

    private static int CategoryRank(string category) => category switch
    {
        "security" or "bug" => 0,
        "performance" => 1,
        "style" => 2,
        "docs" => 3,
        _ => 4,
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4, // info and anything unexpected
    };

    /// <summary>Scope from the fix set's file(s): a single file contributes its top-level
    /// directory (or its extension-less name at the repo root); files in different top-level
    /// directories yield no scope. Sanitized to [a-z0-9.-]; empty result = no scope.</summary>
    private static string? Scope(IReadOnlyList<CommitFixInput> fixes)
    {
        var paths = fixes.Select(f => f.Fix.Proposal.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var topLevels = paths
            .Select(path =>
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return segments.Length > 1 ? segments[0] : Path.GetFileNameWithoutExtension(segments[0]);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (topLevels.Length != 1)
        {
            return null;
        }

        var sanitized = new StringBuilder(topLevels[0].Length);
        foreach (var c in topLevels[0].ToLowerInvariant())
        {
            // ASCII only: conventional-commit tooling and ADO branch UI handle unicode
            // scopes inconsistently — fold anything else to a dash.
            sanitized.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' ? c : '-');
        }

        var scope = sanitized.ToString().Trim('-', '.');
        return scope.Length == 0 ? null : scope;
    }

    private static string Description(IReadOnlyList<CommitFixInput> fixes)
    {
        if (fixes.Count == 1)
        {
            var single = fixes[0];
            if (single.Finding is not null)
            {
                return OneLine(single.Finding.Title).ToLowerInvariant();
            }

            return $"address review thread #{single.Fix.Proposal.SourceThreadId}";
        }

        var paths = fixes.Select(f => f.Fix.Proposal.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length == 1
            ? $"address {fixes.Count} review findings in {OneLine(paths[0])}"
            : $"address {fixes.Count} review findings across {paths.Length} files";
    }

    /// <summary>Collapses CR/LF to spaces: repo-controlled text (paths, titles, rationale)
    /// must never inject extra header/body lines into the composed commit message.</summary>
    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Word-wrap a body bullet at <see cref="BodyWrapWidth"/>; continuation lines are
    /// indented to align under the bullet text.</summary>
    private static void AppendWrapped(StringBuilder sb, string bullet)
    {
        const string indent = "  ";
        var remaining = bullet;
        var first = true;
        while (true)
        {
            var width = first ? BodyWrapWidth : BodyWrapWidth - indent.Length;
            if (remaining.Length <= width)
            {
                if (!first)
                {
                    sb.Append(indent);
                }

                sb.Append(remaining).Append('\n');
                return;
            }

            var breakAt = remaining.LastIndexOf(' ', width);
            if (breakAt <= 0)
            {
                breakAt = width; // no space in range: hard-break the token
            }

            if (!first)
            {
                sb.Append(indent);
            }

            sb.Append(remaining[..breakAt]).Append('\n');
            remaining = remaining[breakAt..].TrimStart();
            first = false;
        }
    }
}
