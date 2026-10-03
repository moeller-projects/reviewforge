using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;
using ReviewForge.Service.Security;

namespace ReviewForge.Service;

public sealed record SubmitReviewRequest(
    [property: Required] string Org,
    [property: Required] string Project,
    [property: Required] string RepositoryId,
    [property: Range(1, int.MaxValue)] int PrId);

public sealed record SubmitReviewResponse(Guid RunId, string StatusUrl);

/// <summary>409 body: another review owns the PR; the competing run id is exposed for clients.</summary>
public sealed record ConflictResponse(string Error, Guid? RunId);

/// <summary>Minimal-API surface: submit, status, discovery, health.</summary>
public static class Endpoints
{
    public static WebApplication MapReviewForgeEndpoints(this WebApplication app)
    {
        // Auth lives on the group metadata: routing and enforcement cannot diverge
        // (endpoint routing matches case-insensitively — see P1-13).
        var reviews = app.MapGroup("/reviews")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        reviews.MapPost("/", SubmitReview)
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("SubmitReview")
            .WithSummary("Enqueue a review run for a pull request")
            .Produces<SubmitReviewResponse>(202)
            .Produces<ConflictResponse>(409)
            .ProducesProblem(400)
            .ProducesProblem(503);

        reviews.MapPost("/discover", DiscoverPullRequests)
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("DiscoverPullRequests")
            .WithTags("Reviews")
            .Produces<DiscoveryReport>(200);

        reviews.MapGet("/{runId:guid}", GetRunStatus)
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy)
            .WithName("GetRunStatus")
            .WithSummary("Run status — in-memory tracker first, then queue/store read-through so finalized runs stay visible after restart")
            .Produces<RunStatus>()
            .ProducesProblem(404);

        return app;
    }

    private static IResult SubmitReview(
        SubmitReviewRequest request,
        IReviewQueue queue,
        RunTracker tracker,
        InFlightClaims claims,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ReviewForge.Service.Endpoints");
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.BadRequest(new {errors});
        }

        var pr = new PrKey(request.Org, request.Project, request.RepositoryId, request.PrId);
        var runId = Guid.NewGuid();
        if (!claims.TryClaim(pr, runId, out var holder))
        {
            logger.LogWarning("review submit conflict for {Pr}: already in flight (run {RunId})", pr, holder);
            return TypedResults.Conflict(new ConflictResponse(
                "a review for this pull request is already in flight", holder));
        }

        // Track-then-enqueue: the run is visible as Queued before the channel write, so a
        // fast worker can never overwrite a fresh RunTracker write with a stale one
        // (P2-25). Roll back the tracker entry if the queue rejects.
        tracker.Set(runId, pr, RunState.Queued);
        var result = queue.TryEnqueue(new ReviewRequest(
            runId, pr, clock.GetUtcNow(), Trigger: EnqueueTrigger.Manual));
        if (!result.Accepted)
        {
            tracker.Remove(runId);
            claims.Release(pr, runId);
            logger.LogWarning("review submit rejected for {Pr}: queue full (depth {Depth})", pr, result.QueueDepth);
            return TypedResults.Problem(
                title: "Review queue full",
                detail: $"Queue depth {result.QueueDepth} of {queue.Capacity}. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("review submitted for {Pr}, run {RunId}", pr, runId);

        return TypedResults.Accepted($"/reviews/{runId}", new SubmitReviewResponse(runId, $"/reviews/{runId}"));
    }

    private static async Task<IResult> DiscoverPullRequests(DiscoveryService discovery, CancellationToken ct)
        => TypedResults.Ok(await discovery.RunSweepAsync(ct));

    /// <summary>Tracker first (authoritative while the process lives); then the durable queue
    /// row (Queued read-through after restart); then the store's run row (Completed/Failed
    /// read-through after restart or tracker retention expiry). Unknown → 404.</summary>
    private static async Task<IResult> GetRunStatus(
        Guid runId, RunTracker tracker, IReviewQueue queue, IFindingStore store, CancellationToken ct)
    {
        if (tracker.Get(runId) is { } status)
        {
            return TypedResults.Ok(status);
        }

        if (queue.TryGetQueued(runId) is { } queued)
        {
            return TypedResults.Ok(new RunStatus(runId, queued.Pr, RunState.Queued, null, queued.EnqueuedAt));
        }

        var run = await store.GetRunAsync(runId, ct);
        return run switch
        {
            null => TypedResults.NotFound(),
            { CompletedAt: null } => TypedResults.Ok(new RunStatus(runId, run.Pr, RunState.Running, null, run.StartedAt)),
            { Success: true } => TypedResults.Ok(new RunStatus(runId, run.Pr, RunState.Completed, null, run.CompletedAt.Value)),
            _ => TypedResults.Ok(new RunStatus(runId, run.Pr, RunState.Failed, null, run.CompletedAt!.Value)),
        };
    }

    private static List<string> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return [.. results.Select(r => r.ErrorMessage ?? "invalid")];
    }
}