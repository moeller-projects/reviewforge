using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class BasicDiscoveryEligibilityTests
{
    [Theory]
    [InlineData(true, "main", "id", null, "Ineligible")]
    [InlineData(false, "other", "id", null, "Ineligible")]
    [InlineData(false, "main", "other", null, "Ineligible")]
    [InlineData(false, "main", "id", 0, "Ineligible")]
    [InlineData(false, "main", "id", null, "Unknown")]
    [InlineData(false, "main", "id", 1, "Eligible")]
    public void Basic_inputs_reuse_ordered_filter(bool draft, string branch, string creator, int? links, string expected)
    {
        var candidate = new PullRequestCandidate(new("o", "p", "r", 1), new(1, "title", null, "head", "base", "", draft, creator, creator), branch, creator, creator);
        var result = DiscoveryFilter.EvaluateBasic(candidate, links, null, new(["main"], ["id"]));
        Assert.Equal(expected, result.Status.ToString());
    }

    [Theory]
    [InlineData("", "id", "name", "head", 1, false, "Unknown")]
    [InlineData("main", "", "", "head", 1, false, "Unknown")]
    [InlineData("main", "id", "", "head", 1, false, "Unknown")]
    [InlineData("main", "", "other", "head", 1, false, "Unknown")]
    [InlineData("main", "id", "name", "", 1, false, "Unknown")]
    [InlineData("", "other", "other", "head", 1, false, "Ineligible")]
    [InlineData("", "", "", "", 0, false, "Ineligible")]
    [InlineData("", "", "", "", 1, true, "Ineligible")]
    [InlineData("other", "", "", "", 1, false, "Ineligible")]
    public void Missing_metadata_is_unknown_unless_a_known_rule_excludes(
        string branch, string creatorId, string creatorName, string head, int links, bool draft, string expected)
    {
        var candidate = new PullRequestCandidate(new("o", "p", "r", 1),
            new(1, "title", null, head, "base", "", draft, creatorId, creatorName), branch, creatorId, creatorName);
        var result = DiscoveryFilter.EvaluateBasic(candidate, links, null, new(["main"], ["id"]));
        Assert.Equal(expected, result.Status.ToString());
    }

    [Theory]
    [InlineData(null, null, "Unknown")]
    [InlineData(0, null, "Ineligible")]
    [InlineData(1, null, "Unknown")]
    [InlineData(1, false, "Ineligible")]
    [InlineData(1, true, "Eligible")]
    public void Same_head_requires_human_comment_facts(int? links, bool? comments, string expected)
    {
        var candidate = new PullRequestCandidate(new("o", "p", "r", 1), new(1, "title", null, "head", "base", "", false, "id", "name"), "main", "id", "name");
        var result = DiscoveryFilter.EvaluateBasic(candidate, links, "head", new(["main"], []), comments);
        Assert.Equal(expected, result.Status.ToString());
        Assert.NotEmpty(result.Reason);
    }
}
