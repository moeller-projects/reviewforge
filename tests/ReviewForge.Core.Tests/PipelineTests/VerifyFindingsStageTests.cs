using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests.PipelineTests;

public sealed class VerifyFindingsStageTests : IDisposable
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);
    private readonly string _RepoDir;

    public VerifyFindingsStageTests()
    {
        _RepoDir = Path.Combine(Path.GetTempPath(), "reviewforge-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_RepoDir, "src"));
        File.WriteAllLines(
            Path.Combine(_RepoDir, "src", "A.cs"),
            ["line one", "var query = raw;", "line three", "line four"]);
    }

    public void Dispose() => Directory.Delete(_RepoDir, recursive: true);

    private static RichFinding Finding(string key, string severity = "high", string rule = "security/sql-injection")
        => new()
        {
            RuleId = rule,
            Title = "Use a parameterized query",
            Severity = severity,
            Category = "security",
            Description = "Raw SQL concatenation",
            Anchor = new FindingAnchor("src/A.cs", 2, 2),
            DedupeKey = key,
        };

    private ReviewContext Ctx(params RichFinding[] findings)
    {
        return new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Repository = new() {RepoDir = _RepoDir},
            Validation = new() {AcceptedFindings = findings},
            Reasoning = new()
            {
                Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []},
            },
        };
    }

    private static VerifyFindingsStage Stage(ScriptedChatClient chat, VerifyFindingsOptions? options = null)
        => new(
            new FakeChatClientFactory(chat),
            options ?? new VerifyFindingsOptions {Enabled = true},
            NullLogger<VerifyFindingsStage>.Instance);

    [Fact]
    public async Task Confirmed_verdicts_keep_all_findings()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text(
            """[{"key":"k1","verdict":"confirmed","reason":"real issue"}]"""));
        var ctx = Ctx(Finding("k1"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["k1"], ctx.Validation.AcceptedFindings.Select(f => f.DedupeKey));
    }

    [Fact]
    public async Task Rejected_verdict_removes_finding()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text(
            """[{"key":"k1","verdict":"rejected","reason":"context disproves it"},{"key":"k2","verdict":"confirmed","reason":"real"}]"""));
        var ctx = Ctx(Finding("k1"), Finding("k2"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["k2"], ctx.Validation.AcceptedFindings.Select(f => f.DedupeKey));
    }

    [Fact]
    public async Task Unknown_key_in_verdict_is_ignored_and_missing_key_is_kept()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text(
            """[{"key":"ghost","verdict":"rejected","reason":"not ours"}]"""));
        var ctx = Ctx(Finding("k1"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["k1"], ctx.Validation.AcceptedFindings.Select(f => f.DedupeKey));
    }

    [Fact]
    public async Task Malformed_then_valid_retry_applies_verdicts()
    {
        var chat = new ScriptedChatClient(
            ScriptedChatClient.Text("I cannot help with that."),
            ScriptedChatClient.Text("""[{"key":"k1","verdict":"rejected","reason":"misread"}]"""));
        var ctx = Ctx(Finding("k1"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, chat.Calls);
        Assert.Empty(ctx.Validation.AcceptedFindings);
    }

    [Fact]
    public async Task Malformed_twice_fails_open_keeping_everything()
    {
        var chat = new ScriptedChatClient(
            ScriptedChatClient.Text("not json"),
            ScriptedChatClient.Text("still not json"));
        var ctx = Ctx(Finding("k1"), Finding("k2"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, chat.Calls);
        Assert.Equal(2, ctx.Validation.AcceptedFindings.Count);
    }

    [Fact]
    public async Task Verifier_exception_fails_open()
    {
        var chat = new ThrowingChatClient();
        var stage = new VerifyFindingsStage(
            new FakeChatClientFactory(chat),
            new VerifyFindingsOptions {Enabled = true},
            NullLogger<VerifyFindingsStage>.Instance);
        var ctx = Ctx(Finding("k1"));

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Fact]
    public async Task Host_shutdown_cancellation_propagates()
    {
        var chat = new ThrowingChatClient(new OperationCanceledException());
        var stage = new VerifyFindingsStage(
            new FakeChatClientFactory(chat),
            new VerifyFindingsOptions {Enabled = true},
            NullLogger<VerifyFindingsStage>.Instance);
        var ctx = Ctx(Finding("k1"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => stage.ExecuteAsync(ctx, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Disabled_stage_makes_no_model_call()
    {
        var chat = new ScriptedChatClient();
        var stage = new VerifyFindingsStage(
            new FakeChatClientFactory(chat),
            new VerifyFindingsOptions {Enabled = false},
            NullLogger<VerifyFindingsStage>.Instance);
        var ctx = Ctx(Finding("k1"));

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, chat.Calls);
        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Fact]
    public async Task Zero_findings_makes_no_model_call()
    {
        var chat = new ScriptedChatClient();

        await Stage(chat).ExecuteAsync(Ctx(), CancellationToken.None);

        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Trivial_diff_runs_are_skipped()
    {
        var chat = new ScriptedChatClient();
        var ctx = Ctx(Finding("k1"));
        ctx.Reasoning.Result!.ReviewDepth = "trivial diff — no agent run";

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, chat.Calls);
        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Fact]
    public async Task Homoglyph_only_findings_make_no_model_call()
    {
        var chat = new ScriptedChatClient();
        var ctx = Ctx(Finding("k1", rule: "homoglyph/cyrillic-a"));

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(0, chat.Calls);
        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Fact]
    public async Task Cap_leaves_unverified_findings_kept_highest_severity_first()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("[]"));
        var findings = Enumerable.Range(1, 5)
            .Select(i => Finding($"k{i}", severity: i <= 2 ? "critical" : "low"))
            .ToArray();
        var ctx = Ctx(findings);

        await Stage(chat, new VerifyFindingsOptions {Enabled = true, MaxFindings = 2})
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(5, ctx.Validation.AcceptedFindings.Count); // none verified → all kept
        var prompt = chat.Received[0].Last().Text;
        Assert.Contains("key: k1", prompt);
        Assert.Contains("key: k2", prompt);
        Assert.DoesNotContain("key: k5", prompt); // beyond the cap
    }

    [Fact]
    public async Task Prompt_embeds_numbered_file_slice_with_anchor_marked()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("[]"));
        var ctx = Ctx(Finding("k1"));

        await Stage(chat, new VerifyFindingsOptions {Enabled = true, ContextLines = 3})
            .ExecuteAsync(ctx, CancellationToken.None);

        var prompt = chat.Received[0].Last().Text;
        Assert.Contains(">> 2: var query = raw;", prompt);
        Assert.Contains("   1: line one", prompt);
        Assert.Contains("<pr-supplied-data>", prompt);
    }

    [Fact]
    public async Task Verifier_runs_on_fast_tier()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("[]"));
        var factory = new FakeChatClientFactory(chat, model: "strong-model", fastModel: "fast-model");
        var stage = new VerifyFindingsStage(
            factory,
            new VerifyFindingsOptions {Enabled = true},
            NullLogger<VerifyFindingsStage>.Instance);
        var ctx = Ctx(Finding("k1"));

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal([Ports.ChatTier.Fast], factory.RequestedTiers);
    }

    [Fact]
    public void Stage_exposes_expected_name()
    {
        var stage = Stage(new ScriptedChatClient());

        Assert.Equal("verify-findings", stage.Name);
    }

    [Fact]
    public async Task Missing_anchor_file_verifies_from_claim_alone()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("[]"));
        var finding = Finding("k1") with {Anchor = new FindingAnchor("src/Gone.cs", 1, 1)};
        var ctx = Ctx(finding);

        await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.Validation.AcceptedFindings);
        Assert.DoesNotContain(">>", chat.Received[0].Last().Text);
    }

    [Fact]
    public async Task Escaping_anchor_path_is_never_read()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "reviewforge-verify-escape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        try
        {
            var chat = new ScriptedChatClient(ScriptedChatClient.Text("[]"));
            var finding = Finding("k1") with {Anchor = new FindingAnchor($"../{Path.GetFileName(sibling)}/evil.cs", 1, 1)};
            var ctx = Ctx(finding);

            await Stage(chat).ExecuteAsync(ctx, CancellationToken.None);
            Assert.DoesNotContain(">>", chat.Received[0].Last().Text);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    private sealed class ThrowingChatClient(Exception? ex = null) : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<Microsoft.Extensions.AI.ChatResponse>(ex ?? new HttpRequestException("provider down"));

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
