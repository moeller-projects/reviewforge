using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ResolvePlannerTests
{
    [Fact]
    public void Plan_clusters_actionable_comments_on_same_file_in_thread_order()
    {
        var comments = new[]
        {
            Comment(12, "src/B.cs", "second"),
            Comment(3, "src/A.cs", "first"),
            Comment(8, "src/A.cs", "another")
        };
        var verdicts = comments.Select(c => new ThreadVerdict(c.ThreadId, TriageVerdict.Actionable, $"{c.ThreadId}:1", "high")).ToArray();

        var plan = ResolvePlanner.Plan(comments, verdicts, ["src/A.cs", "src/B.cs"], 10, 10);

        Assert.Equal([3, 8, 12], plan.Fixes.SelectMany(f => f.ThreadIds));
        var cluster = Assert.Single(plan.Fixes, f => f.CandidateFiles.SequenceEqual(["src/A.cs"]));
        Assert.Equal([3, 8], cluster.ThreadIds);
        Assert.Contains("Thread #3: first", cluster.RequestText);
        Assert.Contains("Thread #8: another", cluster.RequestText);
        Assert.Equal(["src/A.cs", "src/B.cs"], plan.WritableFiles.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Plan_defers_threads_after_thread_budget_deterministically()
    {
        var comments = new[] { Comment(2, "a.cs", "a"), Comment(1, "b.cs", "b") };
        var verdicts = comments.Select(c => new ThreadVerdict(c.ThreadId, TriageVerdict.Actionable, "file:1", "high")).ToArray();

        var plan = ResolvePlanner.Plan(comments, verdicts, ["a.cs", "b.cs"], 1, 10);

        Assert.Equal([2], plan.Fixes.SelectMany(f => f.ThreadIds).ToArray());
        Assert.Equal((1, "thread budget"), Assert.Single(plan.Deferred));
    }

    [Fact]
    public void Plan_defers_new_file_after_writable_file_budget()
    {
        var comments = new[] { Comment(1, "a.cs", "a"), Comment(2, "b.cs", "b") };
        var verdicts = comments.Select(c => new ThreadVerdict(c.ThreadId, TriageVerdict.Actionable, "file:1", "high")).ToArray();

        var plan = ResolvePlanner.Plan(comments, verdicts, ["a.cs", "b.cs"], 10, 1);

        Assert.Single(plan.Fixes);
        Assert.Equal("a.cs", plan.Fixes[0].CandidateFiles.Single());
        Assert.Equal((2, "writable-file budget"), Assert.Single(plan.Deferred));
    }

    [Fact]
    public void Plan_excludes_unauthorized_unanchored_and_unchanged_comments()
    {
        var comments = new[]
        {
            Comment(1, "a.cs", "allowed", allowed: true),
            Comment(2, "a.cs", "unauthorized", allowed: false),
            Comment(3, null, "general", allowed: true),
            Comment(4, "other.cs", "outside", allowed: true)
        };
        var verdicts = comments.Select(c => new ThreadVerdict(c.ThreadId, TriageVerdict.Actionable, "evidence", "high")).ToArray();

        var plan = ResolvePlanner.Plan(comments, verdicts, ["a.cs"], 10, 10);

        Assert.Equal([1], plan.Fixes.SelectMany(f => f.ThreadIds));
        Assert.Empty(plan.Deferred);
    }

    private static ResolvableComment Comment(int id, string? file, string text, bool allowed = true)
        => new(id, file is null ? null : new ThreadAnchor(file, 1, 1), "requester", "Requester", [], text, allowed);
}
