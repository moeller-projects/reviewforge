using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public class FixCommandDetectorTests
{
    private const string CreatorId = "creator-1";
    private const string CreatorName = "PR Author";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");

    private static ThreadComment Author(string text, int minutes) =>
        new(CreatorId, CreatorName, IsBot: false, text, T0.AddMinutes(minutes));

    private static ThreadComment Other(string text, int minutes) =>
        new("someone-else", "Reviewer", IsBot: false, text, T0.AddMinutes(minutes));

    private static ThreadComment Bot(string text, int minutes) =>
        new("bot-1", "reviewforge bot", IsBot: true, text, T0.AddMinutes(minutes));

    private static ReviewThread Thread(
        int id,
        IReadOnlyList<ThreadComment> comments,
        ReviewThreadStatus status = ReviewThreadStatus.Active,
        ThreadAnchor? anchor = null)
        => new(id, null, status, comments, anchor ?? new ThreadAnchor("src/A.cs", 3, 3));

    [Fact]
    public void Author_command_is_detected_with_anchor_instruction_and_quote()
    {
        var threads = new[]
        {
            Thread(7,
                [Other("this is wrong", 1), Author("  /RF FIX please use the constant", 2)]),
        };
        var commands = FixCommandDetector.Scan(threads, CreatorId, CreatorName, watermark: null);
        var command = Assert.Single(commands);
        Assert.Equal(7, command.ThreadId);
        Assert.Equal("src/A.cs", command.Anchor.FilePath);
        Assert.Equal(3, command.Anchor.StartLine);
        Assert.Equal("please use the constant", command.Instruction);
        Assert.Equal("this is wrong", command.QuotedComment);
    }

    [Fact]
    public void Bare_command_has_null_instruction()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("/rf fix", 1)])], CreatorId, CreatorName, watermark: null);
        var command = Assert.Single(commands);
        Assert.Null(command.Instruction);
    }

    [Fact]
    public void Command_matches_by_display_name()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("/rf fix", 1)])], "someone-else", CreatorName, watermark: null);
        Assert.Single(commands);
    }

    [Fact]
    public void Non_author_command_is_ignored()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Other("/rf fix", 1)])], CreatorId, CreatorName, watermark: null);
        Assert.Empty(commands);
    }

    [Fact]
    public void Non_command_last_comment_is_ignored()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("looks fine", 1)])], CreatorId, CreatorName, watermark: null);
        Assert.Empty(commands);
    }

    [Fact]
    public void Fixed_and_closed_threads_are_ignored()
    {
        foreach (var status in new[] {ReviewThreadStatus.Fixed, ReviewThreadStatus.Closed})
        {
            var commands = FixCommandDetector.Scan(
                [Thread(7, [Author("/rf fix", 1)], status)], CreatorId, CreatorName, watermark: null);
            Assert.Empty(commands);
        }
    }

    [Fact]
    public void Bot_last_comment_makes_the_thread_ineligible()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("/rf fix", 1), Bot("working on it", 2)])],
            CreatorId, CreatorName, watermark: null);
        Assert.Empty(commands);
    }

    [Fact]
    public void Command_older_than_watermark_is_ignored()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("/rf fix", 1)])], CreatorId, CreatorName, T0.AddMinutes(5));
        Assert.Empty(commands);
    }

    [Fact]
    public void Command_newer_than_watermark_is_detected()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [Author("/rf fix", 10)])], CreatorId, CreatorName, T0.AddMinutes(5));
        Assert.Single(commands);
    }

    [Fact]
    public void Thread_without_anchor_is_ignored()
    {
        var thread = new ReviewThread(7, null, ReviewThreadStatus.Active,
            [Author("/rf fix", 1)], Anchor: null);
        var commands = FixCommandDetector.Scan([thread], CreatorId, CreatorName, watermark: null);
        Assert.Empty(commands);
    }

    [Fact]
    public void Command_on_our_own_bot_thread_skips_bot_text_and_quotes_the_human_comment()
    {
        var thread = new ReviewThread(9, "dedupe-key", ReviewThreadStatus.Active,
            [Bot("### finding body text", 1), Author("please address this", 2), Author("/rf fix", 3)],
            new ThreadAnchor("src/B.cs", 8, 8));
        var commands = FixCommandDetector.Scan([thread], CreatorId, CreatorName, watermark: null);
        var command = Assert.Single(commands);
        Assert.Equal(9, command.ThreadId);
        Assert.Equal("please address this", command.QuotedComment);
    }

    [Fact]
    public void Command_on_pure_bot_thread_quotes_nothing()
    {
        var thread = new ReviewThread(9, "dedupe-key", ReviewThreadStatus.Active,
            [Bot("### finding body text", 1), Author("/rf fix", 2)],
            new ThreadAnchor("src/B.cs", 8, 8));
        var command = Assert.Single(FixCommandDetector.Scan([thread], CreatorId, CreatorName, watermark: null));
        Assert.Equal(string.Empty, command.QuotedComment);
    }

    [Fact]
    public void Quoted_comment_skips_earlier_commands_and_is_bounded()
    {
        var longText = new string('x', FixCommandDetector.MaxQuotedCommentChars + 50);
        var threads = new[]
        {
            Thread(7, [Other(longText, 1), Author("/rf fix", 2), Author("/rf fix again", 3)]),
        };
        var command = Assert.Single(FixCommandDetector.Scan(threads, CreatorId, CreatorName, watermark: null));
        Assert.Equal(longText[..FixCommandDetector.MaxQuotedCommentChars] + "…", command.QuotedComment);
    }

    [Fact]
    public void Thread_with_no_comments_is_ignored()
    {
        var commands = FixCommandDetector.Scan(
            [Thread(7, [])], CreatorId, CreatorName, watermark: null);
        Assert.Empty(commands);
    }
}
