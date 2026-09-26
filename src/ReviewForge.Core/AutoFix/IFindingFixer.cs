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
    string FilePath,          // repo-relative, normalized
    string[] FileLines,       // 0-based; Finding.Anchor is 1-based into this array
    DiffIndex Diff);

/// <summary>Deterministic = rule-registered fixer. LlmCommanded = agent-drafted after an
/// explicit /rf fix command by the PR author. (LlmAutonomous is reserved — see the
/// AutoFix implementation plan appendix.)</summary>
public enum FixOrigin
{
    Deterministic,
    LlmCommanded
}

/// <summary>A proposed fix: replace Proposal range [StartLine..EndLine] (1-based inclusive)
/// of FilePath with Replacement ('\n'-joined; "" deletes the range).</summary>
public sealed record FixProposal(
    string FilePath,
    int StartLine,            // 1-based, inclusive
    int EndLine,              // 1-based, inclusive
    string Replacement,       // exact text, '\n'-joined; "" deletes the range
    string Rationale,         // one sentence for the suggestion comment
    FixOrigin Origin = FixOrigin.Deterministic,
    int? SourceThreadId = null);   // set for LlmCommanded fixes

/// <summary>A fix that passed all gates this run and will be published as a suggestion.</summary>
public sealed record AppliedFix(
    string DedupeKey,         // finding key, or "thread-{ThreadId}" for commanded fixes
    FixProposal Proposal,
    string VerifierName)
{
    /// <summary>Prefix of audit-only keys for commanded fixes; never a finding identity.</summary>
    public const string CommandKeyPrefix = "thread-";
}
