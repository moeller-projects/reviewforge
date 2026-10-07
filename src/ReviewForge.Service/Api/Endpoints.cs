using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
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

        app.MapPost("/resolutions", SubmitResolution)
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("SubmitResolution")
            .WithSummary("Enqueue a resolution run for a pull request")
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

        // MCP surface for agent-chat clients: same submission/status logic as the endpoints
        // above (RunSubmissionService). The MCP transport endpoint is a raw RequestDelegate —
        // endpoint filters never run on it, so X-Api-Key enforcement is a route-branch
        // middleware with identical semantics (ApiKeyGate). The status policy applies: every
        // JSON-RPC message is a POST, so the submit budget would strangle an agent session;
        // per-PR claims and queue capacity still bound enqueue abuse.
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase),
            branch => branch.UseMiddleware<ApiKeyMiddleware>());
        app.MapMcp("/mcp")
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy);

        return app;
    }

    private static IResult SubmitReview(SubmitReviewRequest request, RunSubmissionService runs)
        => Submit(request, RunKind.Review, runs);

    private static IResult SubmitResolution(
        SubmitReviewRequest request,
        RunSubmissionService runs,
        IOptions<ResolveOptions> resolveOptions)
        => resolveOptions.Value.Enabled
            ? Submit(request, RunKind.Resolve, runs)
            : TypedResults.Problem(title: "Resolve pipeline disabled", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Submit(SubmitReviewRequest request, RunKind kind, RunSubmissionService runs)
        => runs.Submit(request, kind) switch
        {
            SubmitOutcome.Invalid(var errors) => TypedResults.BadRequest(new {errors}),
            SubmitOutcome.Conflict(var holder) => TypedResults.Conflict(new ConflictResponse(
                "a run for this pull request is already in flight", holder)),
            SubmitOutcome.QueueFull(var depth, var capacity) => TypedResults.Problem(
                title: "Review queue full",
                detail: $"Queue depth {depth} of {capacity}. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
            SubmitOutcome.Accepted(var runId, var statusUrl) => TypedResults.Accepted(
                statusUrl, new SubmitReviewResponse(runId, statusUrl)),
            _ => throw new UnreachableException(),
        };

    private static async Task<IResult> DiscoverPullRequests(DiscoveryService discovery, CancellationToken ct)
        => TypedResults.Ok(await discovery.RunSweepAsync(ct));

    /// <summary>Read-through lives in <see cref="RunSubmissionService.GetStatusAsync"/>:
    /// tracker → queue row → store row; unknown → 404.</summary>
    private static async Task<IResult> GetRunStatus(Guid runId, RunSubmissionService runs, CancellationToken ct)
        => await runs.GetStatusAsync(runId, ct) is { } status
            ? TypedResults.Ok(status)
            : TypedResults.NotFound();
}