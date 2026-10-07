using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class AdoTimeTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ToUtc_normalizes_to_zero_offset(DateTimeKind kind)
    {
        var dt = new DateTime(2026, 9, 17, 12, 0, 0, kind);

        var result = AdoTime.ToUtc(dt);

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(2026, result.Year);
    }

    [Fact]
    public void Pat_authored_fixit_is_not_classified_as_bot_and_remains_detectable()
    {
        const string identity = "pat-user";
        var timestamp = DateTimeOffset.UtcNow;
        var isBot = AdoCommentBotClassifier.IsBot(identity, identity, "/fixit");
        var thread = new ReviewThread(
            17,
            null,
            ReviewThreadStatus.Active,
            [new ThreadComment(identity, "PAT user", isBot, "/fixit", timestamp)],
            new ThreadAnchor("src/file.cs", 1, 1));

        var commands = FixCommandDetector.Scan([thread], identity, watermark: null);

        Assert.Single(commands);
        Assert.Equal(17, commands[0].ThreadId);
    }

    [Fact]
    public void Bot_comment_classifier_requires_matching_identity_and_reviewforge_preamble()
    {
        const string identity = "pat-user";
        var botComment = $"{CommentFormatter.BotPreamble}\n\nreview result";

        Assert.True(AdoCommentBotClassifier.IsBot(identity, identity, botComment));
        Assert.False(AdoCommentBotClassifier.IsBot("other-user", identity, botComment));
        Assert.False(AdoCommentBotClassifier.IsBot(identity, identity, null));
    }
}