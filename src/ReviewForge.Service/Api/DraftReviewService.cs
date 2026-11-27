using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

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

public sealed record PublishFindingSelection(string FindingId, long ExpectedDecisionVersion);
public sealed record PublishSelectedDraftRequest(string HeadSha, IReadOnlyList<PublishFindingSelection>? Selections);
public sealed record ReconcileDraftRequest(IReadOnlyList<string> FindingIds);
public enum PublishFindingStatus { Posted, AlreadyPosted, Failed, Unknown, NotAttempted }
public sealed record PublishFindingOutcome(string FindingId, PublishFindingStatus Status, int? ThreadId = null);
public sealed record PublishSelectedDraftResult(bool Accepted, string? ErrorCode, IReadOnlyList<PublishFindingOutcome> Findings)
{
    public int PostedCount => Findings.Count(f => f.Status == PublishFindingStatus.Posted);
    public int AlreadyPostedCount => Findings.Count(f => f.Status == PublishFindingStatus.AlreadyPosted);
    public int FailedCount => Findings.Count(f => f.Status == PublishFindingStatus.Failed);
    public int UnknownCount => Findings.Count(f => f.Status == PublishFindingStatus.Unknown);
    public int NotAttemptedCount => Findings.Count(f => f.Status == PublishFindingStatus.NotAttempted);
}

/// <summary>Durable query and explicit publication operations for review drafts.</summary>
public sealed class DraftReviewService(
    IFindingStore store,
    IPullRequestSource source,
    InFlightClaims claims,
    RunSubmissionService runs,
    IFindingDecisionStore decisions)
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

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = new ClaimHeartbeat(claims, run.Pr, claimId, claims.Ttl / 3).RunUntilCancelled(heartbeatStop.Token);
        try
        {
            var currentPr = await source.GetPullRequestAsync(run.Pr, ct).ConfigureAwait(false);
            if (!string.Equals(currentPr.SourceCommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase))
                return Rejected("pull request head changed; enqueue a new review draft");

            var result = await PublishRowsAsync(run,
                rows.Where(r => selectedIds is null || selectedIds.Contains(r.DedupeKey))
                    .Select(r => (r.DedupeKey, r)).ToArray(), claimId, throwOnFailure: true, ct).ConfigureAwait(false);
            return new(result.Accepted, result.PostedCount, result.AlreadyPostedCount, result.ErrorCode);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            claims.Release(run.Pr, claimId);
        }
    }

    /// <summary>Technician boundary: missing selection is never publish-all. Approval versions are
    /// checked while holding the same PR claim used by decision mutations.</summary>
    public async Task<PublishSelectedDraftResult> PublishSelectedAsync(Guid runId, PublishSelectedDraftRequest request, CancellationToken ct)
    {
        var selections = request.Selections;
        PublishSelectedDraftResult Reject(string code) => new(false, code,
            selections?.Where(s => s is not null).Select(s => new PublishFindingOutcome(s.FindingId, PublishFindingStatus.NotAttempted)).ToArray() ?? []);
        if (selections is null || selections.Count == 0) return Reject("selection-required");
        if (selections.Any(s => s is null || string.IsNullOrWhiteSpace(s.FindingId)) ||
            selections.Select(s => s.FindingId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            return Reject("invalid-selection");
        ReviewRun? run;
        try
        {
            run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null)
                return Reject(await runs.GetStatusAsync(runId, ct).ConfigureAwait(false) is null
                    ? "run-not-found" : "run-not-publishable");
        }
        catch (Exception) { return Reject("publication-unavailable"); }
        if (run.Pipeline != nameof(RunKind.ReviewDraft) || run.CompletedAt is null || !run.Success)
            return Reject("run-not-publishable");
        var claimId = Guid.NewGuid();
        var pr = run.Pr;
        if (!claims.TryClaim(pr, claimId, out _)) return Reject("busy");
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = new ClaimHeartbeat(claims, pr, claimId, claims.Ttl / 3).RunUntilCancelled(heartbeatStop.Token);
        try
        {
            // Re-read durable state only after acquiring exclusion, not from a stale browser projection.
            run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Pipeline != nameof(RunKind.ReviewDraft) || run.CompletedAt is null || !run.Success)
                return Reject("run-not-publishable");
            var status = await runs.GetStatusAsync(runId, ct).ConfigureAwait(false);
            if (status is null || status.State != RunState.Completed || status.Kind != RunKind.ReviewDraft)
                return Reject("run-not-publishable");
            if (!string.Equals(run.HeadSha, request.HeadSha, StringComparison.Ordinal)) return Reject("head-changed");
            var findings = FindingDecisionService.ResolveFindings(run);
            var latest = await decisions.GetFindingDecisionsAsync(runId, ct).ConfigureAwait(false);
            var selected = new List<(string Id, StoredFinding Row)>();
            foreach (var selection in selections)
            {
                var finding = findings.FirstOrDefault(f => string.Equals(f.Id, selection.FindingId, StringComparison.Ordinal));
                if (finding is null) return Reject("invalid-selection");
                if (!finding.PublicationEligible) return Reject("ineligible-finding");
                var decision = latest.FirstOrDefault(d => string.Equals(d.FindingId, finding.Id, StringComparison.Ordinal));
                if (decision is null || decision.State != FindingDecisionState.Approved ||
                    decision.Version != selection.ExpectedDecisionVersion || decision.HeadSha != run.HeadSha)
                    return Reject("approval-conflict");
                selected.Add((finding.Id, finding.AcceptedFinding!));
            }
            if (selected.Select(s => s.Row.DedupeKey).Distinct(StringComparer.Ordinal).Count() != selected.Count)
                return Reject("invalid-selection");
            return await PublishRowsAsync(run, selected, claimId, throwOnFailure: false, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No host write occurs outside PublishRowsAsync, which tracks ambiguous outcomes itself.
            return Reject("publication-unavailable");
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            claims.Release(pr, claimId);
        }
    }

    /// <summary>Explicit read-and-audit reconciliation only. Never posts, reopens, votes or
    /// requires the PR to remain at its old head; the aim is to recover a previous outcome.</summary>
    public async Task<PublishSelectedDraftResult> ReconcileSelectedAsync(Guid runId, ReconcileDraftRequest request, CancellationToken ct)
    {
        var ids = request.FindingIds;
        PublishSelectedDraftResult Reject(string code) => new(false, code, []);
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return Reject("invalid-selection");
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return Reject("run-not-found");
        if (run.Pipeline != nameof(RunKind.ReviewDraft) || run.CompletedAt is null || !run.Success) return Reject("run-not-publishable");
        var selected = FindingDecisionService.ResolveFindings(run).Where(f => ids.Contains(f.Id, StringComparer.Ordinal)).ToArray();
        if (selected.Length != ids.Count || selected.Any(f => f.AcceptedFinding is null)) return Reject("invalid-selection");
        var claimId = Guid.NewGuid();
        if (!claims.TryClaim(run.Pr, claimId, out _)) return Reject("busy");
        var outcomes = new List<PublishFindingOutcome>();
        try
        {
            var threads = await source.GetThreadsAsync(run.Pr, ct).ConfigureAwait(false);
            foreach (var finding in selected)
            {
                if (!claims.Renew(run.Pr, claimId)) return new(false, "busy", outcomes);
                var row = finding.AcceptedFinding!;
                if (row.Published)
                    outcomes.Add(new(finding.Id, PublishFindingStatus.AlreadyPosted, row.ThreadId));
                else if (threads.FirstOrDefault(t => t.DedupeKey == row.DedupeKey) is { } thread)
                {
                    await PersistPublicationAsync(runId, row.DedupeKey, thread.Id, newlyPosted: false).ConfigureAwait(false);
                    outcomes.Add(new(finding.Id, PublishFindingStatus.AlreadyPosted, thread.Id));
                }
                else outcomes.Add(new(finding.Id, PublishFindingStatus.Unknown));
            }
            return new(outcomes.All(o => o.Status == PublishFindingStatus.AlreadyPosted),
                outcomes.Any(o => o.Status == PublishFindingStatus.Unknown) ? "publication-not-observed" : null, outcomes);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new(false, "reconciliation-unavailable", outcomes);
        }
        finally { claims.Release(run.Pr, claimId); }
    }

    private async Task<PublishSelectedDraftResult> PublishRowsAsync(ReviewRun run,
        IReadOnlyList<(string Id, StoredFinding Row)> selected, Guid claimId, bool throwOnFailure, CancellationToken ct)
    {
        var outcomes = selected.Select(s => new PublishFindingOutcome(s.Id, PublishFindingStatus.NotAttempted)).ToArray();
        PublishSelectedDraftResult Result(string? code = null) => new(code is null, code, outcomes);
        var index = 0;
        var externalStarted = false;
        string? intentKey = null;
        int? observedThreadId = null;
        try
        {
            if (!claims.Renew(run.Pr, claimId)) return Result("busy");
            var initialPr = await source.GetPullRequestAsync(run.Pr, ct).ConfigureAwait(false);
            if (!string.Equals(initialPr.SourceCommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase))
                return Result("head-changed");
            var currentThreads = await source.GetThreadsAsync(run.Pr, ct).ConfigureAwait(false);
            var completedKeys = new Dictionary<string, int?>(StringComparer.Ordinal);
            for (; index < selected.Count; index++)
            {
                externalStarted = false;
                intentKey = null;
                observedThreadId = null;
                if (!claims.Renew(run.Pr, claimId)) return Result("busy");
                var row = selected[index].Row;
                if (row.Published || completedKeys.ContainsKey(row.DedupeKey))
                {
                    outcomes[index] = new(selected[index].Id, PublishFindingStatus.AlreadyPosted,
                        row.ThreadId ?? completedKeys.GetValueOrDefault(row.DedupeKey));
                    continue;
                }
                var existing = currentThreads.FirstOrDefault(t => string.Equals(t.DedupeKey, row.DedupeKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    await PersistPublicationAsync(run.Id, row.DedupeKey, existing.Id, newlyPosted: false).ConfigureAwait(false);
                    outcomes[index] = new(selected[index].Id, PublishFindingStatus.AlreadyPosted, existing.Id);
                    completedKeys[row.DedupeKey] = existing.Id;
                    continue;
                }
                // A prior durable intent with no confirmed thread is an unknown outcome,
                // not permission to retry the host write while thread visibility may lag.
                if (row.PublicationAttempted ||
                    run.FindingObservations?.Any(o => o.DedupeKey == row.DedupeKey && o.PublicationAttempted) == true)
                {
                    outcomes[index] = new(selected[index].Id, PublishFindingStatus.Unknown);
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                if (!claims.Renew(run.Pr, claimId)) return Result("busy");
                var currentPr = await source.GetPullRequestAsync(run.Pr, ct).ConfigureAwait(false);
                if (!string.Equals(currentPr.SourceCommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase))
                    return Result("head-changed");
                if (!claims.Renew(run.Pr, claimId)) return Result("busy");
                var finding = JsonSerializer.Deserialize<RichFinding>(row.FindingJson!, JsonOptions)
                    ?? throw new InvalidOperationException("invalid stored finding");
                intentKey = row.DedupeKey;
                await SetPublicationIntentAsync(run.Id, intentKey, attempted: true).ConfigureAwait(false);
                if (!claims.Renew(run.Pr, claimId)) return Result("busy");
                var pinned = await source.GetPullRequestAsync(run.Pr, ct).ConfigureAwait(false);
                if (!string.Equals(pinned.SourceCommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase)) return Result("head-changed");
                if (!claims.Renew(run.Pr, claimId)) return Result("busy");
                // From the moment the host call begins, any exception is ambiguous: reconcile on retry.
                externalStarted = true;
                var threadId = finding.Anchor is not null && !finding.AnchorDowngraded
                    ? await source.PostFindingThreadAsync(run.Pr, finding, ct).ConfigureAwait(false)
                    : await source.PostGeneralCommentAsync(run.Pr, CommentFormatter.FormatFinding(finding), row.DedupeKey, ct).ConfigureAwait(false);
                observedThreadId = threadId;
                await PersistPublicationAsync(run.Id, row.DedupeKey, threadId, newlyPosted: true).ConfigureAwait(false);
                outcomes[index] = new(selected[index].Id, PublishFindingStatus.Posted, threadId);
                completedKeys[row.DedupeKey] = threadId;
            }
            return Result(outcomes.Any(o => o.Status == PublishFindingStatus.Unknown) ? "publication-unknown" : null);
        }
        catch (Exception)
        {
            if (throwOnFailure) throw;
            if (index < outcomes.Length)
                outcomes[index] = new(selected[index].Id, externalStarted ? PublishFindingStatus.Unknown : PublishFindingStatus.Failed, observedThreadId);
            return Result(externalStarted ? "publication-unknown" : "publication-failed");
        }
        finally
        {
            // A failed pin check, lost claim or disconnect before the host call is not
            // ambiguous. Use independent bounded audit IO, even if the browser cancelled.
            // A cleanup failure propagates as unavailable; the durable intent remains safe.
            if (intentKey is not null && !externalStarted)
                await SetPublicationIntentAsync(run.Id, intentKey, attempted: false).ConfigureAwait(false);
        }
    }

    private async Task SetPublicationIntentAsync(Guid runId, string key, bool attempted)
    {
        using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.SetFindingPublicationAttemptedAsync(runId, key, attempted, audit.Token).ConfigureAwait(false);
    }

    private async Task PersistPublicationAsync(Guid runId, string key, int threadId, bool newlyPosted)
    {
        // Browser disconnect must not discard a successful external write. Bound audit IO independently.
        using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.RecordFindingPublicationAsync(runId, key, threadId, newlyPosted, audit.Token).ConfigureAwait(false);
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