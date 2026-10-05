using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ResolveCommandTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string CreatorId = "creator-1";

    [Fact]
    public void Scan_accepts_creator_commands_case_insensitively_and_extracts_requests()
    {
        var threads = new[]
        {
            Thread(7, "  /RESOLVE  ", PublishedAt),
            Thread(8, "/resolve Please fix the null check", PublishedAt),
        };

        var commands = ResolveCommandDetector.Scan(threads, CreatorId, watermark: null);

        Assert.Collection(commands,
            command => Assert.Equal(new ResolveCommand(7, string.Empty, PublishedAt), command),
            command => Assert.Equal(new ResolveCommand(8, "Please fix the null check", PublishedAt), command));
    }

    [Fact]
    public void Scan_ignores_ineligible_threads_and_non_commands()
    {
        var threads = new[]
        {
            new ReviewThread(1, null, ReviewThreadStatus.Fixed, [Comment(CreatorId, false, "/resolve fix")]),
            new ReviewThread(2, null, ReviewThreadStatus.Closed, [Comment(CreatorId, false, "/resolve fix")]),
            new ReviewThread(3, null, ReviewThreadStatus.Active, []),
            new ReviewThread(4, null, ReviewThreadStatus.Active, [Comment(CreatorId, true, "/resolve fix")]),
            new ReviewThread(5, null, ReviewThreadStatus.Active, [Comment("other-user", false, "/resolve fix")]),
            Thread(6, "/resolve fix", PublishedAt.AddMinutes(-1)),
            Thread(7, "/rf resolvex is not a command", PublishedAt),
            Thread(8, "please /resolve fix", PublishedAt),
        };

        var commands = ResolveCommandDetector.Scan(threads, CreatorId, PublishedAt.AddMinutes(-1));

        Assert.Empty(commands);
    }

    private static ReviewThread Thread(int id, string text, DateTimeOffset publishedAt)
        => new(id, null, ReviewThreadStatus.Active, [Comment(CreatorId, false, text, publishedAt)]);

    private static ThreadComment Comment(string authorId, bool isBot, string text, DateTimeOffset? publishedAt = null)
        => new(authorId, authorId, isBot, text, publishedAt ?? PublishedAt);
}