using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>Structured result of the <c>enqueue_review</c> MCP tool; <see cref="Error"/>
/// carries the human/agent-readable rejection reason when <see cref="Accepted"/> is false.</summary>
public sealed record EnqueueReviewResult(bool Accepted, Guid? RunId, string? StatusUrl, string? Error);

/// <summary>One open pull request as reported by the <c>list_open_prs</c> MCP tool; the agent
/// filters/selects from these fields and enqueues via <c>enqueue_review</c>.</summary>
public sealed record OpenPrSummary(
    string Org,
    string Project,
    string RepositoryId,
    int PrId,
    string Title,
    string CreatorName,
    string CreatorId,
    bool IsDraft,
    string SourceBranch,
    string TargetBranch);

/// <summary>Bounded result of the <c>list_open_prs</c> MCP tool. When <see cref="Truncated"/>
/// is true the agent should call again with a larger offset (or narrow with the
/// project/repositoryId filters) to reach the remaining pull requests.</summary>
public sealed record OpenPrListResult(int TotalCount, bool Truncated, IReadOnlyList<OpenPrSummary> PullRequests);

/// <summary>
/// MCP tools for agent-chat clients (served at <c>/mcp</c>, Streamable HTTP). Thin wrappers
/// over <see cref="RunSubmissionService"/> — enqueue/status semantics live there exactly once,
/// shared with the REST endpoints.
/// </summary>
[McpServerToolType]
public sealed class ReviewForgeMcpTools(
    RunSubmissionService runs,
    IPullRequestSource source,
    IOptions<ResolveOptions> resolveOptions)
{
    [McpServerTool(Name = "enqueue_review"),
     Description("Enqueue an automated review run for a pull request. Returns the run id and a "
                 + "status URL on success, or an error explaining the rejection (already in flight, "
                 + "queue full, invalid request). Poll progress with get_review_status.")]
    public EnqueueReviewResult EnqueueReview(
        [Description("Azure DevOps organization name")]
        string org,
        [Description("Azure DevOps project name")]
        string project,
        [Description("Repository id (name or GUID)")]
        string repositoryId,
        [Description("Pull request number")] int prId)
        => Submit(org, project, repositoryId, prId, RunKind.Review);

    [McpServerTool(Name = "enqueue_resolution"),
     Description("Enqueue an automated resolution run for a pull request. The resolve pipeline "
                 + "must be enabled. Returns the run id and a status URL on success, or an error "
                 + "explaining the rejection (pipeline disabled, already in flight, queue full, "
                 + "invalid request). Poll progress with get_review_status.")]
    public EnqueueReviewResult EnqueueResolution(
        [Description("Azure DevOps organization name")]
        string org,
        [Description("Azure DevOps project name")]
        string project,
        [Description("Repository id (name or GUID)")]
        string repositoryId,
        [Description("Pull request number")] int prId)
        => resolveOptions.Value.Enabled
            ? Submit(org, project, repositoryId, prId, RunKind.Resolve)
            : new EnqueueReviewResult(false, null, null, "resolve pipeline disabled");

    private EnqueueReviewResult Submit(string org, string project, string repositoryId, int prId, RunKind kind)
        => runs.Submit(new SubmitReviewRequest(org, project, repositoryId, prId), kind) switch
        {
            SubmitOutcome.Accepted(var runId, var statusUrl) => new EnqueueReviewResult(true, runId, statusUrl, null),
            SubmitOutcome.Conflict(var holder) => new EnqueueReviewResult(
                false, holder, null, "a run for this pull request is already in flight"),
            SubmitOutcome.QueueFull(var depth, var capacity) => new EnqueueReviewResult(
                false, null, null, $"review queue full (depth {depth} of {capacity}); retry shortly"),
            SubmitOutcome.Invalid(var errors) => new EnqueueReviewResult(
                false, null, null, $"invalid request: {string.Join("; ", errors)}"),
            _ => throw new UnreachableException(),
        };

    [McpServerTool(Name = "get_review_status"),
     Description("Get the status of a review run (Queued, Running, Completed, Skipped, Failed). "
                 + "Returns null when the run id is unknown.")]
    public async Task<RunStatus?> GetReviewStatus(
        [Description("Run id returned by enqueue_review")]
        Guid runId,
        CancellationToken cancellationToken)
        => await runs.GetStatusAsync(runId, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "list_open_prs"),
     Description("List open (active) pull requests across the Azure DevOps organization, "
                 + "optionally restricted to one project and/or repository GUID. Select from the "
                 + "results by author, title, draft status or branch — e.g. all PRs, the oldest "
                 + "one, or those from a colleague — then enqueue reviews with enqueue_review. "
                 + "Within one repository a lower prId is the older PR. The result is bounded; "
                 + "when truncated is true, call again with a larger offset (or narrow the "
                 + "filters) to reach the rest.")]
    public async Task<OpenPrListResult> ListOpenPrs(
        [Description("Optional: restrict to a single Azure DevOps project")]
        string? project = null,
        [Description("Optional: restrict to a single repository GUID (the repositoryId value returned by this tool)")]
        string? repositoryId = null,
        [Description("Maximum pull requests to return (1-500, default 100)")]
        int maxResults = 100,
        [Description("Number of matching pull requests to skip before returning results (default 0)")]
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var candidates = await source.GetOpenPullRequestsAsync(cancellationToken).ConfigureAwait(false);
        List<PullRequestCandidate> filtered =
        [
            .. candidates
                .Where(c => project is null
                            || string.Equals(c.Key.Project, project, StringComparison.OrdinalIgnoreCase))
                .Where(c => repositoryId is null
                            || string.Equals(c.Key.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Key.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key.RepositoryId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key.PrId),
        ];
        var take = Math.Clamp(maxResults, 1, 500);
        var skip = Math.Max(offset, 0);
        return new OpenPrListResult(
            filtered.Count,
            filtered.Count > skip + take,
            [
                .. filtered.Skip(skip).Take(take).Select(c => new OpenPrSummary(
                    c.Key.Org,
                    c.Key.Project,
                    c.Key.RepositoryId,
                    c.Key.PrId,
                    c.Pr.Title,
                    c.CreatorName,
                    c.CreatorId,
                    c.Pr.IsDraft,
                    ShortBranch(c.Pr.SourceRefName),
                    ShortBranch(c.TargetBranch))),
            ]);
    }

    private static string ShortBranch(string? refName)
        => refName is null ? string.Empty
            : refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..]
            : refName;
}
