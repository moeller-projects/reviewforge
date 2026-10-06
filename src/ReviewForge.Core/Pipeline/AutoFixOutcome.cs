using ReviewForge.Core.AutoFix;

namespace ReviewForge.Core.Pipeline;

/// <summary>Deterministic and commanded fixes prepared for publication.</summary>
public sealed record AutoFixOutcome
{
    public IReadOnlyList<AppliedFix> AppliedFixes { get; init; } = [];
    public string? PushedHeadSha { get; init; }
    public IReadOnlyList<FixCommand> FixCommands { get; init; } = [];
    public IReadOnlyList<(int ThreadId, string Text)> FixCommandReplies { get; init; } = [];
}
