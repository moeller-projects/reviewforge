namespace ReviewForge.Core.Domain;

/// <summary>A human decision, independent of model verification and external publication.</summary>
public enum FindingDecisionState
{
    Pending,
    Approved,
    Rejected,
    Deferred,
}

/// <summary>An immutable, revision-scoped decision event. Version zero represents no stored
/// decision; persisted events start at one. Actor records the identity actually established
/// by authentication (a shared-key session is not a named human).</summary>
public sealed record FindingDecision
{
    public const int MaxReasonLength = 2000;
    public const int MaxActorLength = 128;

    public Guid RunId { get; }
    public string FindingId { get; }
    public string HeadSha { get; }
    public FindingDecisionState State { get; }
    public string? Reason { get; }
    public string Actor { get; }
    public DateTimeOffset DecidedAt { get; }
    public long Version { get; }

    private FindingDecision(Guid runId, string findingId, string headSha, FindingDecisionState state,
        string? reason, string actor, DateTimeOffset decidedAt, long version)
    {
        RunId = runId;
        FindingId = findingId;
        HeadSha = headSha;
        State = state;
        Reason = reason;
        Actor = actor;
        DecidedAt = decidedAt;
        Version = version;
    }

    public static FindingDecision Record(Guid runId, string findingId, string headSha, FindingDecisionState state,
        string? reason, string actor, DateTimeOffset decidedAt, long version)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run identity is required.", nameof(runId));
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (state == FindingDecisionState.Rejected && reason is null)
            throw new ArgumentException("Rejection requires a reason.", nameof(reason));
        if (reason?.Length > MaxReasonLength)
            throw new ArgumentException($"Reason must not exceed {MaxReasonLength} characters.", nameof(reason));
        if (actor.Length > MaxActorLength)
            throw new ArgumentException($"Actor must not exceed {MaxActorLength} characters.", nameof(actor));
        return new FindingDecision(runId, findingId, headSha, state, reason, actor, decidedAt, version);
    }
}
