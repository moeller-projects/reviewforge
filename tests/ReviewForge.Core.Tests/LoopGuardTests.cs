using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class LoopGuardTests
{
    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static AutoFixOptions Options(string? email = "reviewforge@example.com")
        => new()
        {
            Enabled = true,
            PublishMode = AutoFixOptions.ModeCommitOnHead,
            CommitAuthorName = "reviewforge[bot]",
            CommitAuthorEmail = email,
        };

    private static string Message(string trailer)
        => $"fix(src): subject\n\nbody text\n\n{trailer}\n";

    [Fact]
    public void Guard_accepts_only_when_all_conjuncts_match()
    {
        var info = new TipCommitInfo("reviewforge@example.com", Message($"ReviewForge-Run: {RunId:D}"));

        Assert.True(LoopGuard.IsBotAuthoredHead(info, Options()));
    }

    [Fact]
    public void Guard_matches_author_email_case_insensitively()
    {
        var info = new TipCommitInfo("ReviewForge@Example.COM", Message($"ReviewForge-Run: {RunId:D}"));

        Assert.True(LoopGuard.IsBotAuthoredHead(info, Options()));
    }

    [Fact]
    public void Guard_rejects_a_different_author_email_with_a_valid_trailer()
    {
        var info = new TipCommitInfo("attacker@example.com", Message($"ReviewForge-Run: {RunId:D}"));

        Assert.False(LoopGuard.IsBotAuthoredHead(info, Options()));
    }

    [Fact]
    public void Guard_rejects_the_bot_email_without_a_trailer()
    {
        var info = new TipCommitInfo("reviewforge@example.com", "fix(src): subject\n\nno trailers here\n");

        Assert.False(LoopGuard.IsBotAuthoredHead(info, Options()));
    }

    [Fact]
    public void Guard_is_inert_without_a_configured_bot_email()
    {
        var info = new TipCommitInfo("reviewforge@example.com", Message($"ReviewForge-Run: {RunId:D}"));

        Assert.False(LoopGuard.IsBotAuthoredHead(info, Options(email: null)));
    }

    [Fact]
    public void Guard_is_inert_outside_commit_on_head_mode()
    {
        var options = new AutoFixOptions
        {
            Enabled = true,
            PublishMode = AutoFixOptions.ModeSuggestion,
            CommitAuthorEmail = "reviewforge@example.com",
        };
        var info = new TipCommitInfo("reviewforge@example.com", Message($"ReviewForge-Run: {RunId:D}"));

        Assert.False(LoopGuard.IsBotAuthoredHead(info, options));
    }

    [Fact]
    public void Trailer_parse_rejects_a_mention_in_prose()
    {
        var info = $"fix: use the ReviewForge-Run: {RunId:D} format\n\nsome body\n";

        Assert.False(LoopGuard.TryParseRunTrailer(info, out _));
    }

    [Fact]
    public void Trailer_parse_rejects_a_trailer_line_quoted_in_the_body()
    {
        var message = $"fix: subject\n\nquoted: ReviewForge-Run: {RunId:D}\n\nReviewForge-Run: not-a-guid\n";

        Assert.False(LoopGuard.TryParseRunTrailer(message, out _));
    }

    [Fact]
    public void Trailer_parse_rejects_a_malformed_guid()
    {
        Assert.False(LoopGuard.TryParseRunTrailer(Message("ReviewForge-Run: 12345"), out _));
        Assert.False(LoopGuard.TryParseRunTrailer(Message("ReviewForge-Run: zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz"), out _));
    }

    [Fact]
    public void Trailer_parse_accepts_crlf_messages()
    {
        var message = "fix: subject\r\n\r\nReviewForge-Run: " + RunId.ToString("D") + "\r\n";

        Assert.True(LoopGuard.TryParseRunTrailer(message, out var parsed));
        Assert.Equal(RunId, parsed);
    }

    [Fact]
    public void Trailer_parse_accepts_a_trailer_block_with_other_trailers()
    {
        var message = Message($"ReviewForge-Thread: 7\nReviewForge-Run: {RunId:D}\nSigned-off-by: someone");

        Assert.True(LoopGuard.TryParseRunTrailer(message, out var parsed));
        Assert.Equal(RunId, parsed);
    }

    [Fact]
    public void Trailer_parse_stops_at_the_first_non_trailer_line()
    {
        // A blank line ends the trailer block; a "trailer" above it is body prose.
        var message = $"fix: subject\n\nReviewForge-Run: {RunId:D}\n\nplain body line\n";

        Assert.False(LoopGuard.TryParseRunTrailer(message, out _));
    }

    [Fact]
    public void Round_trip_builder_output_parses()
    {
        var fix = new AppliedFix(
            "key", new FixProposal("src/a.sh", 1, 1, "x", "why"));
        var message = ConventionalCommitBuilder.Build(
            RunId, [new ConventionalCommitBuilder.CommitFixInput(fix, null)]);

        Assert.True(LoopGuard.TryParseRunTrailer(message, out var parsed));
        Assert.Equal(RunId, parsed);
    }
}
