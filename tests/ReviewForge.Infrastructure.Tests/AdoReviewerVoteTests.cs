using ReviewForge.Core.Domain;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class AdoReviewerVoteTests
{
    [Theory]
    [InlineData(ReviewerVote.Approved, 10)]
    [InlineData(ReviewerVote.ApprovedWithSuggestions, 5)]
    [InlineData(ReviewerVote.NoResponse, 0)]
    [InlineData(ReviewerVote.WaitingForAuthor, -5)]
    [InlineData(ReviewerVote.Rejected, -10)]
    public void Maps_to_ado_vote_values(ReviewerVote vote, short expected)
        => Assert.Equal(expected, AdoReviewerVote.ToAdoVote(vote));

    [Fact]
    public void Throws_on_unknown_vote()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AdoReviewerVote.ToAdoVote((ReviewerVote)999));

    [Fact]
    public void Vote_write_is_skipped_when_ado_vote_already_matches()
    {
        Assert.False(AdoReviewerVote.IsChangeRequired(0, ReviewerVote.NoResponse));
        Assert.False(AdoReviewerVote.IsChangeRequired(null, ReviewerVote.NoResponse));
        Assert.False(AdoReviewerVote.IsChangeRequired(-5, ReviewerVote.WaitingForAuthor));
        Assert.True(AdoReviewerVote.IsChangeRequired(0, ReviewerVote.WaitingForAuthor));
        Assert.True(AdoReviewerVote.IsChangeRequired(null, ReviewerVote.Approved));
    }
}
