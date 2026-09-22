using System.Diagnostics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Ports;

/// <summary>
/// Records ADO-call latency and failures for every <see cref="IPullRequestSource"/> method.
/// Provider-neutral: wraps any adapter so the pipeline's instrumentation does not change per host.
/// </summary>
public sealed class InstrumentedPullRequestSource(IPullRequestSource inner) : IPullRequestSource
{
    private const string OperationTag = "ado.operation";

    public Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        => RecordAsync("get_pull_request", () => inner.GetPullRequestAsync(pr, ct));

    public Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
        => RecordAsync("get_open_pull_requests", () => inner.GetOpenPullRequestsAsync(ct));

    public Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
        => RecordAsync("get_linked_work_items", () => inner.GetLinkedWorkItemsAsync(pr, ct));

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct)
        => RecordAsync("get_changed_files", () => inner.GetChangedFilesAsync(pr, ct));

    public Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
        => RecordAsync("get_threads", () => inner.GetThreadsAsync(pr, ct));

    public Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct)
        => RecordAsync("get_current_user", () => inner.GetCurrentUserAsync(ct));

    public Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct)
        => RecordAsync("post_finding_thread", () => inner.PostFindingThreadAsync(pr, finding, ct));

    public Task PostGeneralCommentAsync(PrKey pr, string text, string? dedupeKey, CancellationToken ct)
        => RecordAsync("post_general_comment", () => inner.PostGeneralCommentAsync(pr, text, dedupeKey, ct));

    public Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct)
        => RecordAsync("reply_to_thread", () => inner.ReplyToThreadAsync(pr, threadId, text, ct));

    public Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct)
        => RecordAsync("set_thread_status", () => inner.SetThreadStatusAsync(pr, threadId, status, ct));

    public Task SetReviewerVoteAsync(PrKey pr, string reviewerId, ReviewerVote vote, CancellationToken ct)
        => RecordAsync("set_reviewer_vote", () => inner.SetReviewerVoteAsync(pr, reviewerId, vote, ct));

    private async Task RecordAsync(string operation, Func<Task> call)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ReviewForgeTelemetry.AdoCallFailed.Add(1, new TagList { { OperationTag, operation } });
            throw;
        }

        ReviewForgeTelemetry.AdoCallDurationMilliseconds.Record(sw.ElapsedMilliseconds, new TagList { { OperationTag, operation } });
    }

    private async Task<T> RecordAsync<T>(string operation, Func<Task<T>> call)
    {
        var sw = Stopwatch.StartNew();
        T result;
        try
        {
            result = await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ReviewForgeTelemetry.AdoCallFailed.Add(1, new TagList { { OperationTag, operation } });
            throw;
        }

        ReviewForgeTelemetry.AdoCallDurationMilliseconds.Record(sw.ElapsedMilliseconds, new TagList { { OperationTag, operation } });
        return result;
    }
}
