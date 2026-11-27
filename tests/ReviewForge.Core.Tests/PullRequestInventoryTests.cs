using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class PullRequestInventoryTests
{
    [Fact]
    public void Inventory_preserves_identity_metadata_and_explicit_partial_status()
    {
        var key = new PrKey("org", "project", "repo", 1);
        var pr = new PullRequest(1, "title", null, "head", "base", "", true, "creator", "Creator");
        var candidate = new PullRequestCandidate(key, pr, "main", "creator", "Creator");
        var item = new PullRequestInventoryItem(candidate, "Repository", "https://host/pr/1");
        var inventory = new PullRequestInventory([item], false, ["Project inventory unavailable."]);
        var actual = Assert.Single(inventory.Candidates);
        Assert.Same(candidate, actual.Candidate);
        Assert.Equal("Repository", actual.RepositoryName);
        Assert.Equal("https://host/pr/1", actual.BrowserUrl);
        Assert.False(inventory.IsComplete);
        Assert.Equal("Project inventory unavailable.", Assert.Single(inventory.SourceWarnings));
    }
}
