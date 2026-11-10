using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using WorkItemComment = ReviewForge.Core.Domain.WorkItemComment;

namespace ReviewForge.Infrastructure.Ado;

internal static class AdoWorkItemCommentsPager
{
    internal const int PageSize = 200;

    public static Task<IReadOnlyList<WorkItemComment>> FetchAsync(
        Func<string?, CancellationToken, Task<CommentList>> fetchPage,
        CancellationToken ct)
    {
        var continuationTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        return AdoPageAccumulator.FetchAllAsync(async pageCt =>
        {
            var page = await fetchPage(continuationToken, pageCt).ConfigureAwait(false);
            var nextToken = page.ContinuationToken;
            if (!string.IsNullOrEmpty(nextToken) && !continuationTokens.Add(nextToken))
            {
                throw new InvalidOperationException("ADO returned a repeated work-item comment continuation token.");
            }

            continuationToken = nextToken;
            var comments = page.Comments?
                .Where(comment => comment.IsDeleted != true)
                .Select(comment => new WorkItemComment(
                    comment.CreatedBy?.DisplayName ?? "Unknown",
                    comment.Text ?? string.Empty,
                    ToUtcOffset(comment.CreatedDate),
                    ToUtcOffset(comment.ModifiedDate)))
                .ToArray() ?? [];
            return new AdoPage<WorkItemComment>(comments, !string.IsNullOrEmpty(nextToken));
        }, ct);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
        => value is { } date
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
            : null;
}