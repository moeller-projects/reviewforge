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
