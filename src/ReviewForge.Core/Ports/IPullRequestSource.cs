using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>
/// Provider-neutral access to the pull-request system. ADO is one adapter; GitHub (or any
/// other host) plugs in here without touching the pipeline.
/// </summary>
public interface IPullRequestSource
{
    Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct);

    /// <summary>Org-wide active PRs across all projects and repositories.</summary>
    Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct);

    /// <summary>Linked work items with title, description and acceptance criteria.</summary>
    Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct);

    /// <summary>Authoritative changed-file manifest for the current PR iteration.</summary>
    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct);

    /// <summary>All threads on the PR (bot and human), newest comment last per thread.</summary>
    Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct);

    /// <summary>The identity behind the configured credentials (PAT user).</summary>
    Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct);

    /// <summary>Post an inline (anchored) or general finding thread; returns the new thread id.</summary>
    Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct);

    Task PostGeneralCommentAsync(PrKey pr, string text, CancellationToken ct);

    Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct);

    Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct);

    /// <summary>
    /// Set the reviewer's vote on the PR. The vote is provider-neutral; the adapter maps
    /// <see cref="ReviewerVote"/> to the host system's encoding.
    /// </summary>
    Task SetReviewerVoteAsync(PrKey pr, string reviewerId, ReviewerVote vote, CancellationToken ct);
}