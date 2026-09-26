using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>Result of one constrained fix pass: the agent's final narrative, the editor
/// session (for <see cref="HashLineEditor.GetSessionChange"/> and the revert), and the
/// pass's token usage for telemetry.</summary>
public sealed record FixPassResult(
    ReviewResult Result,
    HashLineEditor Editor,
    long InputTokens,
    long OutputTokens);
