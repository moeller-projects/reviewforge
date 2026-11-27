using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunWorklistGroupingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    [Fact]
    public void Revisions_and_runs_sort_by_observed_chronology_not_hash()
    {
        var older = Item("ffff", Now.AddHours(-2));
        var firstNewHeadRun = Item("0001", Now.AddHours(-1));
        var secondNewHeadRun = Item("0001", Now);
        var group = Assert.Single(RunWorklistGrouping.Group([older, firstNewHeadRun, secondNewHeadRun], false));

        Assert.Equal(["0001", "ffff"], group.Revisions.Select(r => r.HeadSha));
        Assert.Equal([secondNewHeadRun.Run.RunId, firstNewHeadRun.Run.RunId], group.Revisions[0].Runs.Select(r => r.Run.RunId));
        Assert.True(group.Revisions[0].IsCurrentHead);
        Assert.False(group.Revisions[1].IsCurrentHead);
    }

    [Fact]
    public void Full_pr_identity_prevents_cross_repository_project_and_organization_collisions()
    {
        var row = Item("head", Now);
        var groups = RunWorklistGrouping.Group([
            row,
            row with { Pr = Key with { RepositoryId = "other" } },
            row with { Pr = Key with { Project = "other" } },
            row with { Pr = Key with { Org = "other" } },
        ], false);

        Assert.Equal(4, groups.Count);
    }

    [Fact]
    public void Attention_filter_keeps_history_but_only_relevant_revisions_initially_expand()
    {
        var old = Item("old", Now.AddHours(-1));
        var pending = Item("0001", Now) with { PendingDecisions = 2 };
        var noAttention = Item("other", Now) with { Pr = Key with { PrId = 100 } };
        var group = Assert.Single(RunWorklistGrouping.Group([old, pending, noAttention], true));

        Assert.True(group.NeedsAttention);
        Assert.Equal(2, group.Revisions.Count);
        Assert.True(group.Revisions[0].InitiallyExpanded);
        Assert.False(group.Revisions[1].InitiallyExpanded);
        Assert.Single(group.Revisions[1].Runs);
    }

    [Theory]
    [InlineData(RunState.Queued, RunKind.Review, 0, 0, true)]
    [InlineData(RunState.Running, RunKind.Resolve, 0, 0, true)]
    [InlineData(RunState.Failed, RunKind.ReviewDraft, 0, 0, true)]
    [InlineData(RunState.Completed, RunKind.ReviewDraft, 1, 0, true)]
    [InlineData(RunState.Completed, RunKind.ReviewDraft, 0, 1, true)]
    [InlineData(RunState.Completed, RunKind.ReviewDraft, 0, 0, false)]
    [InlineData(RunState.Completed, RunKind.Review, 1, 1, false)]
    [InlineData(RunState.Skipped, RunKind.ReviewDraft, 1, 1, false)]
    public void Attention_distinguishes_execution_from_human_decisions(
        RunState state, RunKind kind, int pending, int approved, bool expected)
    {
        var item = Item("head", Now);
        item = item with { Run = item.Run with { State = state, Kind = kind }, PendingDecisions = pending, ApprovedUnpublished = approved };
        Assert.Equal(expected, item.NeedsAttention);
    }

    [Fact]
    public void Unknown_head_is_separate_and_case_only_sha_differences_share_revision()
    {
        var known = Item("ABCD", Now) with { CurrentHeadSha = "abcd" };
        var same = Item("abcd", Now.AddMinutes(-1));
        var unknown = Item(null, Now.AddMinutes(-2));
        var group = Assert.Single(RunWorklistGrouping.Group([known, same, unknown], false));

        Assert.Equal(2, group.Revisions.Count);
        Assert.True(group.Revisions[0].IsCurrentHead);
        Assert.Equal(2, group.Revisions[0].Runs.Count);
        Assert.Null(group.Revisions[1].HeadSha);
        Assert.False(group.Revisions[1].IsCurrentHead);
    }

    [Fact]
    public void Empty_input_and_claimed_unknown_state_are_handled_without_inventing_completion()
    {
        Assert.Empty(RunWorklistGrouping.Group([], true));
        var item = Item(null, Now);
        item = item with { Run = item.Run with { State = null, IsClaimed = true } };
        Assert.True(item.NeedsAttention);
        Assert.True(Assert.Single(RunWorklistGrouping.Group([item], true)).NeedsAttention);
    }

    private static RunWorklistItem Item(string? head, DateTimeOffset started) => new(Key,
        new DashboardRun(Guid.NewGuid(), head, RunKind.ReviewDraft, RunState.Completed,
            StartedAt: started, CompletedAt: started.AddMinutes(1)),
        Title: "Review this PR", RepositoryName: "Repository", CurrentHeadSha: "0001", PendingDecisions: 0, ApprovedUnpublished: 0);
}
