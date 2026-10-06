using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline;

/// <summary>Resolve-only stage outputs. A review run leaves <see cref="ReviewContext.Resolve"/> null.</summary>
public sealed record ResolveState
{
    public string? RequestedHeadSha { get; set; }
    public DateTimeOffset? ResolveWatermark { get; set; }
    public IReadOnlyList<ResolvableComment> ResolvableComments { get; set; } = [];
    public IReadOnlyList<ThreadVerdict> ThreadVerdicts { get; set; } = [];
    public ResolvePlan? ResolvePlan { get; set; }
    public string ResolveVerificationStatus { get; set; } = "skipped";
    public IReadOnlyList<AppliedResolution> AppliedResolutions { get; set; } = [];
    public IReadOnlyDictionary<int, ResolutionOutcome> ResolutionOutcomes { get; set; } = new Dictionary<int, ResolutionOutcome>();
    public IReadOnlyList<ResolveAction> ResolveActions { get; set; } = [];
    public IReadOnlyDictionary<int, string> ResolutionDetails { get; set; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, HashLineEditor> ResolutionEditors { get; set; } = new Dictionary<int, HashLineEditor>();
}
