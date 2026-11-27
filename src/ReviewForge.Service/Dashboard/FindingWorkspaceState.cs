using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Run-local identity seam shared by queue, inspector and future command surfaces. Never persisted in browser storage.</summary>
public sealed class FindingWorkspaceState
{
    public string? FocusedId { get; set; }
    public string? RangeAnchor { get; private set; }
    public bool Advance { get; set; } = true;
    public bool Complete { get; set; }
    public string? DecisionBlockReason(RunFindingDto finding, string headSha, bool disabled) =>
        DecisionBlockReason(finding, headSha, disabled, Drafts.GetValueOrDefault(finding.FindingId));
    public static string? DecisionBlockReason(RunFindingDto finding, string headSha, bool disabled, FindingEditorDraft? draft)
    {
        if (disabled) return "Actions are unavailable while busy, stale or signed out.";
        if (!finding.CanDecide || string.IsNullOrWhiteSpace(headSha)) return "Only completed draft findings with a reviewed revision can be decided.";
        if (draft is { Dirty: true } && draft.Version != finding.DecisionVersion)
            return "Authoritative decision changed. Review the current version before saving.";
        return null;
    }
    public void AdvanceAfterSave(string savedId, IReadOnlyList<string> previousOrder, IReadOnlyList<RunFindingDto> findings)
    {
        if (!Advance) return;
        var matching = Matching(findings);
        var index = previousOrder.ToList().IndexOf(savedId);
        var next = previousOrder.Skip(index + 1).Concat(previousOrder.Take(Math.Max(index, 0)))
            .FirstOrDefault(id => id != savedId && matching.Any(f => f.FindingId == id && (f.Decision?.State ?? FindingDecisionState.Pending) == FindingDecisionState.Pending));
        Complete = !matching.Any(f => (f.Decision?.State ?? FindingDecisionState.Pending) == FindingDecisionState.Pending);
        if (next is not null) FocusedId = next;
    }
    public string? BatchBlockReason(IReadOnlyList<RunFindingDto> findings, string headSha, bool disabled)
    {
        if (disabled) return "Actions are unavailable while busy, stale or signed out.";
        if (Selected.Count is < 1 or > FindingDecisionService.MaxBatchSize) return "Select between 1 and 500 findings.";
        foreach (var id in Selected)
        {
            var finding = findings.FirstOrDefault(f => f.FindingId == id);
            if (finding is null || DecisionBlockReason(finding, headSha, false) is not null) return "Selection contains an unavailable or changed finding. Review before saving.";
            if (Drafts.GetValueOrDefault(id)?.Dirty == true) return "Save or review unsaved decision drafts before a batch action.";
        }
        return null;
    }
    public BatchFindingDecisionRequest? SnapshotBatch(IReadOnlyList<RunFindingDto> findings, string headSha, FindingDecisionState state, bool disabled)
    {
        if (state is not (FindingDecisionState.Approved or FindingDecisionState.Deferred or FindingDecisionState.Pending)
            || BatchBlockReason(findings, headSha, disabled) is not null) return null;
        return new(headSha, Array.AsReadOnly(findings.Where(f => Selected.Contains(f.FindingId))
            .Select(f => new BatchFindingDecisionSelection(f.FindingId, f.DecisionVersion, state, null)).ToArray()));
    }
    public HashSet<string> Selected { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FindingEditorDraft> Drafts { get; } = new(StringComparer.Ordinal);
    public string Search { get; set; } = "";
    public string Pipeline { get; set; } = "all";
    public string Decision { get; set; } = "all";
    public string Publication { get; set; } = "all";
    public string Severity { get; set; } = "all";
    public string Sort { get; set; } = "severity";
    public static FindingPayloadDto? Payload(RunFindingDto f) => f.Candidate ?? f.AcceptedFinding;
    public static string PipelineLabel(RunFindingDto f) => f.Evidence?.Validation == FindingValidationStatus.Rejected || f.Evidence?.Verification == FindingVerificationStatus.Rejected
        ? "Pipeline-rejected" : f.AcceptedFinding is not null ? "Pipeline-accepted" : "Unavailable";
    public static string PublicationLabel(RunFindingDto f) => f.PublicationUnknown || f.Published is null ? "Unknown" : f.Published == true ? "Posted" : "Unpublished";
    public IReadOnlyList<RunFindingDto> Matching(IReadOnlyList<RunFindingDto> findings) => findings.Where(f =>
        (Pipeline == "all" || PipelineLabel(f) == Pipeline) &&
        (Decision == "all" || (f.Decision?.State ?? FindingDecisionState.Pending).ToString() == Decision) &&
        (Publication == "all" || (Publication == "Previously known" ? f.PreviouslyKnown : PublicationLabel(f) == Publication)) &&
        (Severity == "all" || string.Equals(Payload(f)?.Severity, Severity, StringComparison.OrdinalIgnoreCase)) &&
        (Search.Length == 0 || new[] { Payload(f)?.Title, Payload(f)?.RuleId, Payload(f)?.Anchor?.FilePath }.Any(s => s?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true)))
        .OrderBy(f => Sort == "severity" ? SeverityRank(Payload(f)?.Severity) : 0)
        .ThenBy(f => Payload(f)?.Anchor?.FilePath, StringComparer.Ordinal)
        .ThenBy(f => Payload(f)?.Anchor?.StartLine ?? 0)
        .ThenBy(f => f.FindingId, StringComparer.Ordinal).ToArray();
    private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch { "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };
    public void Select(string id, bool selected, bool range, IReadOnlyList<RunFindingDto> matching)
    {
        var ids = matching.Select(f => f.FindingId).ToList();
        var start = RangeAnchor is null ? -1 : ids.IndexOf(RangeAnchor);
        var end = ids.IndexOf(id);
        if (range && start >= 0 && end >= 0)
            foreach (var key in ids.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1)) { if (selected) Selected.Add(key); else Selected.Remove(key); }
        else { if (selected) Selected.Add(id); else Selected.Remove(id); }
        RangeAnchor = id;
    }
    public void Reconcile(IReadOnlyList<RunFindingDto> findings)
    {
        Selected.RemoveWhere(id => !findings.Any(f => f.FindingId == id));
        if (!findings.Any(f => f.FindingId == FocusedId)) FocusedId = findings.FirstOrDefault()?.FindingId;
    }
}

public sealed class FindingEditorDraft(long version, FindingDecisionState state, string reason)
{
    public long Version { get; set; } = version;
    public FindingDecisionState State { get; set; } = state;
    public string Reason { get; set; } = reason;
    public bool Dirty { get; set; }
}
