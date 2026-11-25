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

public enum BasicEligibilityStatus { Eligible, Ineligible, Unknown }

public sealed record BasicEligibility(BasicEligibilityStatus Status, string Reason);

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
        DiscoveryRules rules,
        bool hasNewHumanCommentsSinceLastRun = false)
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

        if (!hasNewHumanCommentsSinceLastRun
            && string.Equals(candidate.Pr.SourceCommitSha, lastReviewedHeadSha, StringComparison.Ordinal))
        {
            return new DiscoveryDecision(false, "head already reviewed");
        }

        return new DiscoveryDecision(true, string.Empty);
    }

    /// <summary>Read-only basic assessment. Unknown inputs never imply scheduling eligibility;
    /// temporary blockers, backoff and sweep caps are deliberately outside this assessment.</summary>
    public static BasicEligibility EvaluateBasic(PullRequestCandidate candidate, int? linkedWorkItemCount,
        string? lastReviewedHeadSha, DiscoveryRules rules, bool? hasNewHumanCommentsSinceLastRun = null)
    {
        // Neutralize unavailable inputs only while checking for a reliably known exclusion.
        // Missing metadata must not masquerade as a non-matching branch or creator.
        var missingBranch = string.IsNullOrWhiteSpace(candidate.TargetBranch);
        var missingCreatorId = string.IsNullOrWhiteSpace(candidate.CreatorId);
        var missingCreatorName = string.IsNullOrWhiteSpace(candidate.CreatorName);
        var knownInputs = candidate with
        {
            TargetBranch = missingBranch ? rules.TargetBranches.FirstOrDefault() ?? "" : candidate.TargetBranch,
            CreatorId = missingCreatorId ? rules.Creators.FirstOrDefault() ?? "" : candidate.CreatorId,
            CreatorName = missingCreatorName ? rules.Creators.FirstOrDefault() ?? "" : candidate.CreatorName,
        };
        var decision = Evaluate(knownInputs, linkedWorkItemCount ?? 1, null, rules);
        if (!decision.Interesting)
            return new(BasicEligibilityStatus.Ineligible, decision.Reason);
        if (missingBranch)
            return new(BasicEligibilityStatus.Unknown, "Target branch is unknown.");
        if (rules.Creators.Count > 0 && (missingCreatorId || missingCreatorName))
            return new(BasicEligibilityStatus.Unknown, "Creator metadata is incomplete.");
        if (string.IsNullOrWhiteSpace(candidate.Pr.SourceCommitSha))
            return new(BasicEligibilityStatus.Unknown, "Current head is unknown.");
        if (linkedWorkItemCount is null)
            return new(BasicEligibilityStatus.Unknown, "Linked work-item availability is unknown.");
        if (candidate.Pr.SourceCommitSha == lastReviewedHeadSha && hasNewHumanCommentsSinceLastRun is null)
            return new(BasicEligibilityStatus.Unknown, "Head already reviewed; new human-comment activity is unknown.");
        decision = Evaluate(candidate, linkedWorkItemCount.Value, lastReviewedHeadSha, rules,
            hasNewHumanCommentsSinceLastRun ?? false);
        return decision.Interesting
            ? new(BasicEligibilityStatus.Eligible, "Basic filters pass; scheduling is assessed separately.")
            : new(BasicEligibilityStatus.Ineligible, decision.Reason);
    }

    private static string ShortName(string branch)
        => branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? branch["refs/heads/".Length..]
            : branch;
}