namespace ReviewForge.Core.Pipeline;

/// <summary>
/// The PR head moved between context fetch and publication (force-push mid-run).
/// The run aborts before any external write; the sweep re-enqueues the new head.
/// </summary>
public sealed class PrHeadChangedException(string expected, string actual)
    : InvalidOperationException($"PR head changed during review: {expected} → {actual}; aborting before publication")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}