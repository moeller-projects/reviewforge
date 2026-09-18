namespace ReviewForge.Core.Domain;

/// <summary>Outcome of the review gate: run the pipeline or exit early (successfully).</summary>
public sealed record GateDecision(bool ShouldReview, string Reason)
{
    public static GateDecision Review() => new(true, "review required");
    public static GateDecision Skip(string reason) => new(false, reason);
}

/// <summary>
/// Pure gate logic: decides whether the PR requires a review run.
/// Skips drafts and "same head, no new human comments since the last completed run".
/// </summary>
public static class ReviewGate
{
    public static GateDecision Evaluate(
        PullRequest pr,
        PriorRun? priorRun,
        IReadOnlyList<ReviewThread> threads,
        DateTimeOffset now)
    {
        if (pr.IsDraft)
        {
            return GateDecision.Skip("PR is a draft");
        }

        if (priorRun is null || priorRun.HeadSha != pr.SourceCommitSha)
        {
            return GateDecision.Review();
        }

        var newHumanComments = threads
            .SelectMany(t => t.Comments)
            .Any(c => !c.IsBot && c.PublishedAt > priorRun.CompletedAt);

        return newHumanComments
            ? GateDecision.Review()
            : GateDecision.Skip("head already reviewed and no new human comments");
    }
}

/// <summary>Decides full vs follow-up review and extracts pending human replies.</summary>
public static class RunClassifier
{
    public static ReviewKind Classify(PriorRun? priorRun)
        => priorRun is null ? ReviewKind.Full : ReviewKind.FollowUp;

    public static IReadOnlyList<PendingReply> PendingReplies(IReadOnlyList<ReviewThread> threads)
        =>
        [
            .. threads
                .Where(t => t.DedupeKey is not null && t.HasPendingHumanReply)
                .Select(t => new PendingReply(t.Id, t.DedupeKey, t.LastComment!.AuthorName, t.LastComment.Text))
        ];
}