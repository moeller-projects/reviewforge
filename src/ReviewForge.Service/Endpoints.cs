using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
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

/// <summary>Minimal-API surface: submit, status, discovery, health.</summary>
public static class Endpoints
{
    public static WebApplication MapReviewForgeEndpoints(this WebApplication app)
    {
        app.MapPost("/reviews", SubmitReview)
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("SubmitReview")
            .WithSummary("Enqueue a review run for a pull request")
            .Produces<SubmitReviewResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(503);

        app.MapPost("/reviews/discover", DiscoverPullRequests)
            .RequireRateLimiting(ApiKeyOptions.SubmitPolicy)
            .WithName("DiscoverPullRequests")
            .WithTags("Reviews")
            .Produces<DiscoveryReport>(200);

        app.MapGet("/reviews/{runId:guid}", GetRunStatus)
            .WithName("GetRunStatus")
            .Produces<RunStatus>()
            .ProducesProblem(404);


        return app;
    }

    private static IResult SubmitReview(
        SubmitReviewRequest request,
        ReviewQueue queue,
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
            return TypedResults.Conflict(new {error = "a review for this pull request is already in flight", runId = holder});
        }

        var result = queue.TryEnqueue(new ReviewRequest(runId, pr, clock.GetUtcNow()));
        if (!result.Accepted)
        {
            claims.Release(pr, runId);
            logger.LogWarning("review submit rejected for {Pr}: queue full (depth {Depth})", pr, result.QueueDepth);
            return TypedResults.Problem(
                title: "Review queue full",
                detail: $"Queue depth {result.QueueDepth} of {queue.Capacity}. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        tracker.Set(runId, pr, RunState.Queued);
        logger.LogInformation("review submitted for {Pr}, run {RunId}", pr, runId);

        return TypedResults.Accepted($"/reviews/{runId}", new SubmitReviewResponse(runId, $"/reviews/{runId}"));
    }

    private static async Task<IResult> DiscoverPullRequests(DiscoveryService discovery, CancellationToken ct)
        => TypedResults.Ok(await discovery.RunSweepAsync(ct));

    private static IResult GetRunStatus(Guid runId, RunTracker tracker)
        => tracker.Get(runId) is { } status ? TypedResults.Ok(status) : TypedResults.NotFound();

    private static List<string> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return [.. results.Select(r => r.ErrorMessage ?? "invalid")];
    }
}