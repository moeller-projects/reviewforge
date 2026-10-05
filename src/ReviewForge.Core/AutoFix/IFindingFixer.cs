using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.AutoFix;

/// <summary>
/// A deterministic, rule-registered fixer. Implementations are pure: no IO, no clock,
/// no randomness — they read only <see cref="FixContext.FileLines"/> and either return
/// a minimal replacement or null (cannot derive the fix from file content alone; the
/// pipeline then falls back to comment-only publication).
/// </summary>
public interface IFindingFixer
{
    string RuleId { get; }

    /// <summary>Pure: no IO, no clock, no randomness. Null = cannot derive the fix from
    /// file content alone; the pipeline falls back to comment-only publication.</summary>
    FixProposal? TryPropose(FixContext context);
}

/// <summary>Input for one fix attempt: the finding, its file's content lines, and the diff.</summary>
public sealed record FixContext(
    RichFinding Finding,
    string FilePath, // repo-relative, normalized
    string[] FileLines, // 0-based; Finding.Anchor is 1-based into this array
    DiffIndex Diff);

/// <summary>Deterministic = rule-registered fixer. LlmCommanded = agent-drafted after an
/// explicit /fixit command by the PR author. (LlmAutonomous is reserved — see the
/// AutoFix implementation plan appendix.)</summary>
public enum FixOrigin
{
    Deterministic,
    LlmCommanded
}

/// <summary>A proposed fix: replace Proposal range [StartLine..EndLine] (1-based inclusive)
/// of FilePath with Replacement ('\n'-joined; "" deletes the range).</summary>
public sealed record FixProposal
{
    public FixProposal(
        string FilePath,
        int StartLine,
        int EndLine,
        string Replacement,
        string Rationale,
        FixOrigin Origin = FixOrigin.Deterministic,
        int? SourceThreadId = null)
    {
        if ((Origin == FixOrigin.LlmCommanded && SourceThreadId is not > 0)
            || (Origin != FixOrigin.LlmCommanded && SourceThreadId is not null))
        {
            throw new ArgumentException(
                "SourceThreadId must be a positive value if and only if Origin is LlmCommanded");
        }

        this.FilePath = FilePath;
        this.StartLine = StartLine;
        this.EndLine = EndLine;
        this.Replacement = Replacement;
        this.Rationale = Rationale;
        this.Origin = Origin;
        this.SourceThreadId = SourceThreadId;
    }

    public string FilePath { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Replacement { get; init; }
    public string Rationale { get; init; }
    public FixOrigin Origin { get; init; }
    public int? SourceThreadId { get; init; }

    public void Deconstruct(
        out string filePath,
        out int startLine,
        out int endLine,
        out string replacement,
        out string rationale,
        out FixOrigin origin,
        out int? sourceThreadId)
    {
        filePath = FilePath;
        startLine = StartLine;
        endLine = EndLine;
        replacement = Replacement;
        rationale = Rationale;
        origin = Origin;
        sourceThreadId = SourceThreadId;
    }
}

/// <summary>A fix that passed all gates this run and will be published — as a suggestion
/// (Suggestion mode, or degraded) or as a pushed commit (CommitOnHead mode).</summary>
public sealed record AppliedFix(
    string DedupeKey, // finding key, or "thread-{ThreadId}" for commanded fixes
    FixProposal Proposal)
{
    /// <summary>Prefix of audit-only keys for commanded fixes; never a finding identity.</summary>
    public const string CommandKeyPrefix = "thread-";

    /// <summary>CommitOnHead only: the edit was materialized in the private checkout and is
    /// pending commit by stage 7.7. False in Suggestion mode and for degraded fixes.</summary>
    public bool AppliedToTree { get; set; }

    /// <summary>Set by stage 7.7 after the commit landed; null = publish as suggestion.</summary>
    public string? CommitSha { get; set; }

    /// <summary>Subject line of <see cref="CommitSha"/> (reply bodies).</summary>
    public string? CommitSubject { get; set; }
}