using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public class DiscoveryFilterTests
{
    private static readonly DiscoveryRules Rules = new(["main", "develop"], []);

    private static PullRequestCandidate Candidate(
        string branch = "main",
        string creatorId = "u1",
        string creatorName = "User One",
        string headSha = "head-1",
        bool draft = false)
    {
        var key = new PrKey("o", "p", "r", 1);
        var pr = new PullRequest(1, "t", null, headSha, "base", "url", draft);
        return new PullRequestCandidate(key, pr, branch, creatorId, creatorName);
    }

    [Fact]
    public void Draft_is_skipped()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(draft: true), 1, null, Rules);

        Assert.False(decision.Interesting);
        Assert.Equal("draft", decision.Reason);
    }

    [Fact]
    public void Branch_not_in_filter_is_skipped()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(branch: "feature/x"), 1, null, Rules);

        Assert.False(decision.Interesting);
        Assert.Equal("target branch 'feature/x' not in filter", decision.Reason);
    }

    [Fact]
    public void Branch_matches_case_insensitive_short_name()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(branch: "MAIN"), 1, null, Rules);

        Assert.True(decision.Interesting);
    }

    [Fact]
    public void Branch_matches_refs_prefixed_name()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(branch: "refs/heads/develop"), 1, null, Rules);

        Assert.True(decision.Interesting);
    }

    [Fact]
    public void Creator_not_in_filter_is_skipped()
    {
        var rules = new DiscoveryRules(["main"], ["alice"]);
        var decision = DiscoveryFilter.Evaluate(Candidate(creatorId: "bob", creatorName: "Bob"), 1, null, rules);

        Assert.False(decision.Interesting);
        Assert.Equal("creator 'bob' not in filter", decision.Reason);
    }

    [Fact]
    public void Creator_matches_by_id_case_insensitive()
    {
        var rules = new DiscoveryRules(["main"], ["ALICE"]);
        var decision = DiscoveryFilter.Evaluate(Candidate(creatorId: "alice"), 1, null, rules);

        Assert.True(decision.Interesting);
    }

    [Fact]
    public void Creator_matches_by_name_case_insensitive()
    {
        var rules = new DiscoveryRules(["main"], ["Alice Smith"]);
        var decision = DiscoveryFilter.Evaluate(Candidate(creatorId: "u-alice", creatorName: "alice smith"), 1, null, rules);

        Assert.True(decision.Interesting);
    }

    [Fact]
    public void Empty_creator_filter_allows_all()
    {
        var rules = new DiscoveryRules(["main"], []);
        var decision = DiscoveryFilter.Evaluate(Candidate(creatorId: "anyone", creatorName: "Anyone"), 1, null, rules);

        Assert.True(decision.Interesting);
    }

    [Fact]
    public void No_linked_work_items_is_skipped()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(), 0, null, Rules);

        Assert.False(decision.Interesting);
        Assert.Equal("no linked work items", decision.Reason);
    }

    [Fact]
    public void Already_reviewed_head_is_skipped()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(headSha: "abc123"), 1, "abc123", Rules);

        Assert.False(decision.Interesting);
        Assert.Equal("head already reviewed", decision.Reason);
    }

    [Fact]
    public void New_head_is_interesting()
    {
        var decision = DiscoveryFilter.Evaluate(Candidate(headSha: "new-head"), 1, "old-head", Rules);

        Assert.True(decision.Interesting);
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Fact]
    public void Rules_default_max_enqueues_is_20()
        => Assert.Equal(20, new DiscoveryRules([], []).MaxEnqueues);
}