using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class AdoWorkItemCommentsPagerTests
{
    [Fact]
    public async Task FetchAsync_reads_every_page_in_order()
    {
        var calls = new List<string?>();
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var modified = created.AddDays(1);
        var first = new CommentList
        {
            Comments =
            [
                new Comment {Text = "first", CreatedDate = created, ModifiedDate = modified},
                new Comment {Text = "deleted", IsDeleted = true},
            ],
            ContinuationToken = "next",
        };
        var second = new CommentList
        {
            Comments = [new Comment {Text = "second", CreatedDate = created}],
        };

        var comments = await AdoWorkItemCommentsPager.FetchAsync((token, _) =>
        {
            calls.Add(token);
            return Task.FromResult(token is null ? first : second);
        }, CancellationToken.None);

        Assert.Equal(new string?[] {null, "next"}, calls);
        Assert.Equal(new[] {"first", "second"}, comments.Select(comment => comment.Text));
        Assert.Equal("Unknown", comments[0].Author);
        Assert.Equal<DateTimeOffset?>(new DateTimeOffset(created), comments[0].CreatedDate);
        Assert.Equal<DateTimeOffset?>(new DateTimeOffset(modified), comments[0].ModifiedDate);
    }

    [Fact]
    public async Task FetchAsync_fails_on_repeated_continuation_token()
    {
        var page = new CommentList {ContinuationToken = "same"};

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AdoWorkItemCommentsPager.FetchAsync((_, _) => Task.FromResult(page), CancellationToken.None));
    }
}
