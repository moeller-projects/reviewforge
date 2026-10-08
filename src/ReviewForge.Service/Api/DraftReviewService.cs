using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

public sealed record DraftFindingResult(
    string FindingId,
    string RuleId,
    string Title,
    string Severity,
    string Category,
    string Description,
    string? Snippet,
    string? Suggestion,
    FindingAnchor? Anchor,
    bool AnchorDowngraded,
    bool Published,
    int? ThreadId);

public sealed record DraftFindingsResult(
    bool Success,
    string? Error,
    string? ReviewedHeadSha,
    IReadOnlyList<DraftFindingResult> Findings);

public sealed record PublishDraftResult(bool Accepted, int PublishedCount, int AlreadyPublishedCount, string? Error);

/// <summary>Durable query and explicit publication operations for review drafts.</summary>
public sealed class DraftReviewService(
    IFindingStore store,
    IPullRequestSource source,
    InFlightClaims claims,
    RunSubmissionService runs)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DraftFindingsResult> GetFindingsAsync(Guid runId, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return new DraftFindingsResult(false, await MissingRunErrorAsync(runId, ct).ConfigureAwait(false), null, []);
        if (!string.Equals(run.Pipeline, RunKind.ReviewDraft.ToString(), StringComparison.Ordinal))
            return new DraftFindingsResult(false, "run is not a review draft", null, []);
        if (run.CompletedAt is null)
            return new DraftFindingsResult(false, await IncompleteRunErrorAsync(runId, ct).ConfigureAwait(false), run.HeadSha, []);
        if (!run.Success)
            return new DraftFindingsResult(false, "review draft failed", run.HeadSha, []);

        var findings = new List<DraftFindingResult>();
        foreach (var row in run.Findings.Where(f => f.FindingJson is not null))
        {
            var finding = JsonSerializer.Deserialize<RichFinding>(row.FindingJson!, JsonOptions)
                          ?? throw new InvalidOperationException($"stored finding {row.DedupeKey} is invalid");
            findings.Add(new DraftFindingResult(
                row.DedupeKey, finding.RuleId, finding.Title, finding.Severity, finding.Category,
                finding.Description, finding.Snippet, finding.Suggestion, finding.Anchor,
                finding.AnchorDowngraded, row.Published, row.ThreadId));
        }

        return new DraftFindingsResult(true, null, run.HeadSha, findings);
    }

    public async Task<PublishDraftResult> PublishAsync(
        Guid runId,
        IReadOnlyList<string>? findingIds,
        CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return Rejected(await MissingRunErrorAsync(runId, ct).ConfigureAwait(false));
        if (!string.Equals(run.Pipeline, RunKind.ReviewDraft.ToString(), StringComparison.Ordinal))
            return Rejected("run is not a review draft");
        if (run.CompletedAt is null)
            return Rejected(await IncompleteRunErrorAsync(runId, ct).ConfigureAwait(false));
        if (!run.Success)
            return Rejected("review draft failed");

        var rows = run.Findings.Where(f => f.FindingJson is not null).ToArray();
        var selectedIds = findingIds?.ToHashSet(StringComparer.Ordinal);
        if (selectedIds is not null && selectedIds.Count != findingIds!.Count)
            return Rejected("findingIds contains duplicates");
        if (selectedIds is not null && selectedIds.Except(rows.Select(r => r.DedupeKey), StringComparer.Ordinal).Any())
            return Rejected("one or more findingIds do not belong to this draft");

        var claimId = Guid.NewGuid();
        if (!claims.TryClaim(run.Pr, claimId, out var holder))
            return Rejected($"a run for this pull request is already in flight ({holder})");

        try
        {
            var currentPr = await source.GetPullRequestAsync(run.Pr, ct).ConfigureAwait(false);
            if (!string.Equals(currentPr.SourceCommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase))
                return Rejected("pull request head changed; enqueue a new review draft");

            var currentThreads = await source.GetThreadsAsync(run.Pr, ct).ConfigureAwait(false);
            var publishedCount = 0;
            var alreadyPublishedCount = 0;
            foreach (var row in rows.Where(r => selectedIds is null || selectedIds.Contains(r.DedupeKey)))
            {
                if (row.Published)
                {
                    alreadyPublishedCount++;
                    continue;
                }

                var existing = currentThreads.FirstOrDefault(t => string.Equals(
                    t.DedupeKey, row.DedupeKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    await store.MarkFindingPublishedAsync(runId, row.DedupeKey, existing.Id, ct).ConfigureAwait(false);
                    alreadyPublishedCount++;
                    continue;
                }

                if (!claims.Renew(run.Pr, claimId))
                    return new PublishDraftResult(false, publishedCount, alreadyPublishedCount,
                        "pull request claim was lost; retry to publish remaining findings");

                var finding = JsonSerializer.Deserialize<RichFinding>(row.FindingJson!, JsonOptions)
                              ?? throw new InvalidOperationException($"stored finding {row.DedupeKey} is invalid");
                int? threadId = null;
                if (finding.Anchor is not null && !finding.AnchorDowngraded)
                    threadId = await source.PostFindingThreadAsync(run.Pr, finding, ct).ConfigureAwait(false);
                else
                    await source.PostGeneralCommentAsync(run.Pr, CommentFormatter.FormatFinding(finding), row.DedupeKey, ct)
                        .ConfigureAwait(false);

                await store.MarkFindingPublishedAsync(runId, row.DedupeKey, threadId, ct).ConfigureAwait(false);
                publishedCount++;
            }

            return new PublishDraftResult(true, publishedCount, alreadyPublishedCount, null);
        }
        finally
        {
            claims.Release(run.Pr, claimId);
        }
    }

    private static PublishDraftResult Rejected(string error) => new(false, 0, 0, error);

    private async Task<string> MissingRunErrorAsync(Guid runId, CancellationToken ct)
    {
        var status = await runs.GetStatusAsync(runId, ct).ConfigureAwait(false);
        if (status is null)
            return "unknown run id";
        if (status.Kind != RunKind.ReviewDraft)
            return "run is not a review draft";
        return status.State switch
        {
            RunState.Queued or RunState.Running => "review draft is still running",
            RunState.Failed => "review draft failed",
            RunState.Skipped => "review draft was skipped by the gate",
            _ => "review draft findings are not available",
        };
    }

    private async Task<string> IncompleteRunErrorAsync(Guid runId, CancellationToken ct)
    {
        var status = await runs.GetStatusAsync(runId, ct).ConfigureAwait(false);
        return status?.State switch
        {
            RunState.Failed => "review draft failed",
            RunState.Skipped => "review draft was skipped by the gate",
            _ => "review draft is still running",
        };
    }
}