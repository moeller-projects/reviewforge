using System.Diagnostics.CodeAnalysis;

namespace ReviewForge.Core.Domain;

/// <summary>A pull request discovered by an org-wide sweep, before any filter decision.</summary>
[ExcludeFromCodeCoverage]
public sealed record PullRequestCandidate(
    PrKey Key,
    PullRequest Pr,
    string TargetBranch,
    string CreatorId,
    string CreatorName);

/// <summary>Outcome of filtering one candidate: interesting, or a skip reason.</summary>
[ExcludeFromCodeCoverage]
public sealed record DiscoveryDecision(bool Interesting, string Reason);

/// <summary>Org-wide sweep filter configuration.</summary>
[ExcludeFromCodeCoverage]
public sealed record DiscoveryRules(
    IReadOnlyCollection<string> TargetBranches,
    IReadOnlyCollection<string> Creators,
    int MaxEnqueues = 20);

/// <summary>Pure rules deciding whether a discovered PR should be enqueued for review.</summary>
public static class DiscoveryFilter
{
    /// <summary>
    /// Evaluates one candidate against the sweep rules. Skip reasons are checked in order:
    /// draft, target branch, creator, linked work items, already-reviewed head. A candidate is
    /// interesting only when every check passes.
    /// </summary>
    public static DiscoveryDecision Evaluate(
        PullRequestCandidate candidate,
        int linkedWorkItemCount,
        string? lastReviewedHeadSha,
        DiscoveryRules rules)
    {
        if (candidate.Pr.IsDraft)
        {
            return new DiscoveryDecision(false, "draft");
        }

        var branch = ShortName(candidate.TargetBranch);
        if (!rules.TargetBranches.Any(b => string.Equals(ShortName(b), branch, StringComparison.OrdinalIgnoreCase)))
        {
            return new DiscoveryDecision(false, $"target branch '{candidate.TargetBranch}' not in filter");
        }

        if (rules.Creators.Count > 0 &&
            !rules.Creators.Any(c =>
                string.Equals(c, candidate.CreatorId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, candidate.CreatorName, StringComparison.OrdinalIgnoreCase)))
        {
            return new DiscoveryDecision(false, $"creator '{candidate.CreatorId}' not in filter");
        }

        if (linkedWorkItemCount == 0)
        {
            return new DiscoveryDecision(false, "no linked work items");
        }

        if (string.Equals(candidate.Pr.SourceCommitSha, lastReviewedHeadSha, StringComparison.Ordinal))
        {
            return new DiscoveryDecision(false, "head already reviewed");
        }

        return new DiscoveryDecision(true, string.Empty);
    }

    private static string ShortName(string branch)
        => branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? branch["refs/heads/".Length..]
            : branch;
}