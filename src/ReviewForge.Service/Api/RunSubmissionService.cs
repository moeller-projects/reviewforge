using System.ComponentModel.DataAnnotations;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>Outcome of one enqueue attempt. The HTTP endpoint and the MCP tool each map it
/// to their own wire shape so submission semantics exist exactly once.</summary>
public abstract record SubmitOutcome
{
    public sealed record Accepted(Guid RunId, string StatusUrl) : SubmitOutcome;

    public sealed record Conflict(Guid? Holder) : SubmitOutcome;

    public sealed record QueueFull(int Depth, int Capacity) : SubmitOutcome;

    public sealed record Invalid(IReadOnlyList<string> Errors) : SubmitOutcome;
}

/// <summary>
/// Shared run submission and status read-through behind <c>POST /reviews</c> and the
/// <c>/mcp</c> tools. Claim → track → enqueue, with rollback on a full queue; a failed
/// submission never lingers in the tracker or the claim set.
/// </summary>
public sealed class RunSubmissionService(
    IReviewQueue queue,
    RunTracker tracker,
    InFlightClaims claims,
    IFindingStore store,
    TimeProvider clock,
    ILogger<RunSubmissionService> logger)
{
    public SubmitOutcome Submit(SubmitReviewRequest request, RunKind kind)
    {
        var errors = Validate(request);
        if (request.ForceReview && kind != RunKind.Review)
        {
            errors.Add("forceReview is supported only for manual ordinary reviews.");
        }

        if (errors.Count > 0)
        {
            return new SubmitOutcome.Invalid(errors);
        }

        var pr = new PrKey(request.Org, request.Project, request.RepositoryId, request.PrId);
        var runId = Guid.NewGuid();
        if (!claims.TryClaim(pr, runId, out var holder))
        {
            logger.LogWarning("{Kind} submit conflict for {Pr}: already in flight (run {RunId})", kind, pr, holder);
            return new SubmitOutcome.Conflict(holder);
        }

        var queuedRequest = new ReviewRequest(
            runId, pr, clock.GetUtcNow(), Trigger: EnqueueTrigger.Manual, Kind: kind, ForceReview: request.ForceReview);
        tracker.Register(queuedRequest);
        var result = queue.TryEnqueue(queuedRequest);
        if (!result.Accepted)
        {
            tracker.Remove(runId);
            claims.Release(pr, runId);
            logger.LogWarning("{Kind} submit rejected for {Pr}: queue full (depth {Depth})", kind, pr, result.QueueDepth);
            return new SubmitOutcome.QueueFull(result.QueueDepth, queue.Capacity);
        }

        logger.LogInformation("{Kind} submitted for {Pr}, run {RunId}", kind, pr, runId);
        return new SubmitOutcome.Accepted(runId, $"/reviews/{runId}");
    }

    /// <summary>Tracker first (authoritative while the process lives); then the durable queue
    /// row (Queued read-through after restart); then the store's run row (Completed/Failed
    /// read-through after restart or tracker retention expiry). Null when the run is unknown.</summary>
    public async Task<RunStatus?> GetStatusAsync(Guid runId, CancellationToken ct)
    {
        if (tracker.Get(runId) is { } status)
        {
            return status;
        }

        if (queue.TryGetQueued(runId) is { } queued)
        {
            return new RunStatus(runId, queued.Pr, RunState.Queued, null, queued.EnqueuedAt, queued.Kind)
            {
                HeadSha = queued.HeadSha,
                EnqueuedAt = queued.EnqueuedAt,
                Trigger = queued.Trigger,
            };
        }

        var run = await store.GetRunAsync(runId, ct);
        if (run is null) return null;
        var state = run.CompletedAt is null ? RunState.Running : run.Success ? RunState.Completed : RunState.Failed;
        return new RunStatus(runId, run.Pr, state, null, run.CompletedAt ?? run.StartedAt, ParseRunKind(run.Pipeline))
        {
            HeadSha = string.IsNullOrEmpty(run.HeadSha) ? null : run.HeadSha,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
        };
    }

    private static RunKind ParseRunKind(string pipeline)
        => Enum.TryParse<RunKind>(pipeline, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : RunKind.Review;

    private static List<string> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return [.. results.Select(r => r.ErrorMessage ?? "invalid")];
    }
}