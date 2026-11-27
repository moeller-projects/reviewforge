using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Run-local rejection intent. Entries and versions are pinned on opening; confirmation copies reasons.
/// Outcome versions are evidence only, never retry versions. No browser persistence.</summary>
public sealed class RejectionComposerState(string headSha, IReadOnlyList<RunFindingDto> findings)
{
    public string HeadSha { get; } = headSha;
    public string SharedReason { get; set; } = "";
    public IReadOnlyList<RunFindingDto> Entries { get; private set; } = findings.ToArray();
    public Dictionary<string, string> Overrides { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<BatchFindingDecisionItem> Outcomes { get; private set; } = [];
    public bool Attempted { get; private set; }
    public bool HasUnsaved => Entries.Count > 0 && (Attempted || SharedReason.Length > 0 || Overrides.Values.Any(v => v.Length > 0));
    public string EffectiveReason(string id) => (Overrides.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value) ? value : SharedReason).Trim();
    public string? Validation(string id) => EffectiveReason(id).Length switch
    {
        0 => "A rejection reason is required.",
        > FindingDecision.MaxReasonLength => $"Reason must not exceed {FindingDecision.MaxReasonLength} characters after trimming.",
        _ => null
    };
    public string? BlockReason(IReadOnlyList<RunFindingDto> current, string headSha, bool disabled)
    {
        if (disabled) return "Actions are unavailable while busy, stale or signed out.";
        if (Attempted) return "Submission ended. Inspect unresolved outcomes; close and explicitly compose a new batch after reviewing current decisions. Nothing is retried automatically.";
        if (headSha != HeadSha || Entries.Any(e => !current.Any(f => f.FindingId == e.FindingId && f.DecisionVersion == e.DecisionVersion && f.CanDecide)))
            return "Authoritative decisions changed. Reasons and original versions are retained; review before composing a new batch.";
        if (Entries.Any(e => Validation(e.FindingId) is not null)) return "Provide a valid effective reason for every finding.";
        return null;
    }
    public BatchFindingDecisionRequest? Confirm(IReadOnlyList<RunFindingDto> current, string headSha, bool disabled)
    {
        if (BlockReason(current, headSha, disabled) is not null) return null;
        Attempted = true;
        return new(HeadSha, Array.AsReadOnly(Entries.Select(e => new BatchFindingDecisionSelection(e.FindingId,
            e.DecisionVersion, FindingDecisionState.Rejected, EffectiveReason(e.FindingId))).ToArray()));
    }
    public void Apply(BatchFindingDecisionResponse response)
    {
        Outcomes = Array.AsReadOnly(response.Items.ToArray());
        var saved = response.Items.Where(i => i.Status == BatchFindingDecisionStatus.Saved
            && Entries.Any(e => e.FindingId == i.FindingId && i.CurrentVersion == e.DecisionVersion + 1)).Select(i => i.FindingId).ToHashSet(StringComparer.Ordinal);
        Entries = Entries.Where(e => !saved.Contains(e.FindingId)).ToArray();
        foreach (var id in saved) Overrides.Remove(id);
        if (Entries.Count == 0) SharedReason = "";
    }
}
