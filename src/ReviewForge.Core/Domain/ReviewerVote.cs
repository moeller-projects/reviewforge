namespace ReviewForge.Core.Domain;

/// <summary>
/// Provider-neutral reviewer vote. The pipeline decides <i>what</i> the bot's verdict is;
/// each <see cref="ReviewForge.Core.Ports.IPullRequestSource"/> adapter maps the value to
/// its provider's encoding (ADO: Approved=10, ApprovedWithSuggestions=5, NoResponse=0,
/// WaitingForAuthor=-5, Rejected=-10).
/// </summary>
public enum ReviewerVote
{
    /// <summary>Clear any previous vote; the reviewer has not responded.</summary>
    NoResponse,

    /// <summary>Changes look good; no author action required.</summary>
    Approved,

    /// <summary>Approve, but non-blocking suggestions were made.</summary>
    ApprovedWithSuggestions,

    /// <summary>Blocking findings or unanswered threads — the author must act.</summary>
    WaitingForAuthor,

    /// <summary>Changes must not merge in this state.</summary>
    Rejected,
}
