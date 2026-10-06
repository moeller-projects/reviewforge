using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline;

/// <summary>Repository outputs produced before classification and reasoning.</summary>
public sealed record RepoPreparation
{
    public string? RepoDir { get; init; }
    public string DiffText { get; init; } = string.Empty;
    public DiffIndex? Diff { get; init; }
    public IReadOnlyCollection<string>? ReviewableFiles { get; init; }
    public TipCommitInfo? HeadCommitInfo { get; init; }
}
