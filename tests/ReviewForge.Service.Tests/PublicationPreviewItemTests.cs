using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class PublicationPreviewItemTests
{
    private static RunFindingDto Finding => new("id", null, null, null, false,
        new(Guid.NewGuid(), "id", "head", FindingDecisionState.Approved, null, "dashboard-session", DateTimeOffset.UtcNow, 7),
        7, false, null, true, true);

    [Fact]
    public void Preview_keeps_identity_version_and_unavailable_evidence_honest()
    {
        Assert.Equal(new("id", "Finding payload unavailable", "Location unavailable", 7, "Not approved"),
            PublicationPreviewItem.From(Finding, "Not approved"));
    }

    [Theory]
    [InlineData(false, true, "accepted.cs:7")]
    [InlineData(true, true, "General PR comment")]
    [InlineData(false, false, "General PR comment")]
    public void Preview_uses_the_accepted_payload_and_actual_publication_location(bool downgraded, bool anchored, string location)
    {
        var candidate = new FindingPayloadDto("r", "Candidate title", "high", "bug", "candidate", null, null, new("candidate.cs", 1, 1), false);
        var accepted = candidate with { Title = "Accepted title", Anchor = anchored ? new("accepted.cs", 7, 8) : null, AnchorDowngraded = downgraded };
        var preview = PublicationPreviewItem.From(Finding with { Candidate = candidate, AcceptedFinding = accepted }, null);
        Assert.Equal("Accepted title", preview.Title);
        Assert.Equal(location, preview.Location);
        Assert.Equal(7, preview.Version);
        Assert.Null(preview.ExclusionReason);
    }

    [Fact]
    public void Excluded_candidate_without_an_accepted_payload_remains_readable()
    {
        var candidate = new FindingPayloadDto("r", "Rejected candidate", "high", "bug", "claim", null, null, new("candidate.cs", 1, 1), false);
        var preview = PublicationPreviewItem.From(Finding with { Candidate = candidate, PublicationEligible = false }, "Not technically eligible");
        Assert.Equal("Rejected candidate", preview.Title);
        Assert.Equal("candidate.cs:1", preview.Location);
        Assert.Equal("Not technically eligible", preview.ExclusionReason);
    }

    [Fact]
    public void Exclusions_separate_unknown_posted_technical_and_human_states()
    {
        Assert.Null(PublicationPreviewItem.Exclusion(Finding, false));
        Assert.StartsWith("Unknown publication outcome", PublicationPreviewItem.Exclusion(Finding, true));
        Assert.StartsWith("Unknown publication outcome", PublicationPreviewItem.Exclusion(Finding with { PublicationUnknown = true }, false));
        Assert.StartsWith("Unknown publication outcome", PublicationPreviewItem.Exclusion(Finding with { Published = null }, false));
        Assert.Equal("Already posted", PublicationPreviewItem.Exclusion(Finding with { Published = true }, false));
        Assert.Equal("Not technically eligible", PublicationPreviewItem.Exclusion(Finding with { PublicationEligible = false }, false));
        Assert.Equal("Not approved", PublicationPreviewItem.Exclusion(Finding with { Decision = null }, false));
    }
}
