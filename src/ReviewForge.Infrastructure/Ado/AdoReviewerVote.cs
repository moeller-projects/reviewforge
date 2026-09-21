using ReviewForge.Core.Domain;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>
/// Maps the provider-neutral <see cref="ReviewerVote"/> onto Azure DevOps reviewer vote
/// values: Approved=10, ApprovedWithSuggestions=5, NoResponse=0, WaitingForAuthor=-5,
/// Rejected=-10.
/// </summary>
public static class AdoReviewerVote
{
    public static short ToAdoVote(ReviewerVote vote) => vote switch
    {
        ReviewerVote.Approved => 10,
        ReviewerVote.ApprovedWithSuggestions => 5,
        ReviewerVote.NoResponse => 0,
        ReviewerVote.WaitingForAuthor => -5,
        ReviewerVote.Rejected => -10,
        _ => throw new ArgumentOutOfRangeException(nameof(vote), vote, "unknown reviewer vote"),
    };
}
