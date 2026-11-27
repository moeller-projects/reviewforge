using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

public sealed record SubmitReviewRequest(
    [property: Required] string Org,
    [property: Required] string Project,
    [property: Required] string RepositoryId,
    [property: Range(1, int.MaxValue)] int PrId,
    bool ForceReview = false);

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
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("SubmitReview")
            .WithSummary("Enqueue a review run for a pull request")
            .WithDescription("Optional forceReview is a JSON boolean: omitted or false preserves normal same-head suppression; true bypasses only that suppression for a manual ordinary review. Draft PRs still skip. The server fetches the authoritative head.")
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
            .Produces<DiscoveryReport>();

        reviews.MapGet("/dashboard", GetDashboard)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy)
            .WithName("GetDashboard")
            .Produces<DashboardResponse>();

        reviews.MapGet("/history", GetRunHistory)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy)
            .WithName("GetRunHistory")
            .Produces<RunWorklistResponse>()
            .Produces<TechnicianErrorResponse>(400);

        reviews.MapPost("/drafts", SubmitDraft)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .Produces<SubmitReviewResponse>(202)
            .Produces<TechnicianErrorResponse>(503);

        reviews.MapGet("/drafts/capability", (IOptions<ReviewForge.Core.Pipeline.VerifyFindingsOptions> options) =>
                TypedResults.Ok(new DraftCapabilityResponse(options.Value.Enabled,
                    options.Value.Enabled ? null : "Lightweight finding verification is disabled.")))
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy);

        reviews.MapGet("/{runId:guid}/details", GetRunDetails)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy)
            .Produces<RunDetailsResponse>()
            .Produces<TechnicianErrorResponse>(404);

        reviews.MapGet("/{runId:guid}/decisions/history", GetFindingDecisionHistory)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.StatusPolicy)
            .Produces<FindingDecisionHistoryResponse>()
            .Produces<TechnicianErrorResponse>(400)
            .Produces<TechnicianErrorResponse>(404);

        reviews.MapPost("/{runId:guid}/decisions", SetFindingDecision)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .Produces<FindingDecisionResponse>()
            .Produces<FindingDecisionResponse>(400)
            .Produces<FindingDecisionResponse>(404)
            .Produces<FindingDecisionResponse>(409);

        reviews.MapPost("/{runId:guid}/reconcile", ReconcileSelectedDraft)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .Produces<PublishSelectedDraftResult>()
            .Produces<PublishSelectedDraftResult>(400)
            .Produces<PublishSelectedDraftResult>(404)
            .Produces<PublishSelectedDraftResult>(409);

        reviews.MapPost("/{runId:guid}/publish", PublishSelectedDraft)
            .WithMetadata(new DashboardSessionAccess())
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .Produces<PublishSelectedDraftResult>()
            .Produces<PublishSelectedDraftResult>(400)
            .Produces<PublishSelectedDraftResult>(404)
            .Produces<PublishSelectedDraftResult>(409);

        reviews.MapGet("/{runId:guid}", GetRunStatus)
            .WithMetadata(new DashboardSessionAccess())
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

    private static IResult SubmitDraft(SubmitReviewRequest request, RunSubmissionService runs,
        IOptions<ReviewForge.Core.Pipeline.VerifyFindingsOptions> options)
        => options.Value.Enabled ? Submit(request, RunKind.ReviewDraft, runs)
            : TypedResults.Json(new TechnicianErrorResponse("verification-disabled"), statusCode: 503);

    private static async Task<IResult> GetRunDetails(Guid runId, RunDetailsService details, CancellationToken ct)
        => await details.GetAsync(runId, ct) is { } result ? TypedResults.Ok(result)
            : TypedResults.NotFound(new TechnicianErrorResponse("run-not-found"));

    private static async Task<IResult> GetFindingDecisionHistory(Guid runId, string? findingId,
        RunDetailsService details, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(findingId) || findingId.Length > 2048)
            return TypedResults.BadRequest(new TechnicianErrorResponse("invalid-finding-id"));
        return await details.GetHistoryAsync(runId, findingId, ct) is { } history
            ? TypedResults.Ok(history) : TypedResults.NotFound(new TechnicianErrorResponse("finding-not-found"));
    }

    private static async Task<IResult> SetFindingDecision(Guid runId, SetFindingDecisionRequest request,
        HttpContext http, FindingDecisionService decisions, CancellationToken ct)
    {
        // Header priority matches ApiKeyEndpointFilter. Neither a claimed name nor credential is an actor.
        var actor = !http.Request.Headers.ContainsKey(ApiKeyOptions.HeaderName)
            && http.RequestServices.GetRequiredService<DashboardSessions>().IsValid(http.User)
            ? "dashboard-session" : "header-key";
        var result = await decisions.DecideAsync(runId, request.FindingId, request.HeadSha, request.State,
            request.Reason, actor, request.ExpectedVersion, ct);
        var response = new FindingDecisionResponse(result.Status, result.CurrentVersion,
            result.Decision is null ? null : FindingDecisionDto.From(result.Decision));
        return TypedResults.Json(response, statusCode: result.Status switch
        {
            FindingDecisionStatus.Saved => 200,
            FindingDecisionStatus.Conflict => 409,
            FindingDecisionStatus.RunNotFound or FindingDecisionStatus.FindingNotFound => 404,
            _ => 400,
        });
    }

    private static async Task<IResult> PublishSelectedDraft(Guid runId, PublishSelectedDraftRequest request,
        DraftReviewService drafts, CancellationToken ct)
    {
        var result = await drafts.PublishSelectedAsync(runId, request, ct);
        return TypedResults.Json(result, statusCode: result.Accepted ? 200 : result.ErrorCode switch
        {
            "run-not-found" => 404,
            "selection-required" or "invalid-selection" or "ineligible-finding" or "run-not-publishable" => 400,
            _ => 409,
        });
    }

    private static async Task<IResult> ReconcileSelectedDraft(Guid runId, ReconcileDraftRequest request,
        DraftReviewService drafts, CancellationToken ct)
    {
        var result = await drafts.ReconcileSelectedAsync(runId, request, ct);
        return TypedResults.Json(result, statusCode: result.ErrorCode switch
        {
            "run-not-found" => 404,
            "invalid-selection" or "run-not-publishable" => 400,
            "busy" => 409,
            "reconciliation-unavailable" => 503,
            _ => 200,
        });
    }

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

    private static async Task<IResult> GetRunHistory(RunWorklistQueryService query, CancellationToken ct,
        int offset = 0, int count = 50, bool attentionOnly = false,
        string? org = null, string? project = null, string? repositoryId = null, int? prId = null)
    {
        var hasScope = org is not null || project is not null || repositoryId is not null || prId is not null;
        if (offset is < 0 or > 10_000 || count is < 1 or > 100 || hasScope &&
            (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repositoryId) || prId is null or < 1))
            return TypedResults.BadRequest(new TechnicianErrorResponse("invalid-history-query"));
        PrKey? pr = hasScope ? new(org!, project!, repositoryId!, prId!.Value) : null;
        return TypedResults.Ok(await query.GetAsync(offset, count, pr, attentionOnly, ct));
    }

    private static async Task<IResult> GetDashboard(DashboardQueryService query, CancellationToken ct)
        => TypedResults.Ok(await query.GetAsync(ct));

    private static async Task<IResult> DiscoverPullRequests(DiscoveryService discovery, CancellationToken ct)
        => TypedResults.Ok(await discovery.RunSweepAsync(ct));

    /// <summary>Read-through lives in <see cref="RunSubmissionService.GetStatusAsync"/>:
    /// tracker → queue row → store row; unknown → 404.</summary>
    private static async Task<IResult> GetRunStatus(Guid runId, RunSubmissionService runs, CancellationToken ct)
        => await runs.GetStatusAsync(runId, ct) is { } status
            ? TypedResults.Ok(RunDetailsService.SafeStatus(status))
            : TypedResults.NotFound(new TechnicianErrorResponse("run-not-found"));
}