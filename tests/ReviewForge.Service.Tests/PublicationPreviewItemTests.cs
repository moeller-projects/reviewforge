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
