using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.AutoFix.Fixers;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class AutoFixStageTests : IDisposable
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    private readonly string _Root;

    public AutoFixStageTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-autofix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private string WriteFile(string rel, params string[] lines)
    {
        var path = Path.Combine(_Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Explicit LF seed: fix output must be asserted against a platform-independent
        // line ending, not Environment.NewLine from File.WriteAllLines.
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static RichFinding Finding(string rule, string path, int line, string key)
        => new()
        {
            RuleId = rule, Title = "t", Severity = "medium", Category = "bug", Description = "d",
            Anchor = new FindingAnchor(path, line, line), DedupeKey = key,
        };

    private ReviewContext Ctx(PullRequest? pr = null)
        => new(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = pr ?? new PullRequest(
                1, "t", null, "head-sha", "base", "url", false, "creator-1", "PR Author"),
            RepoDir = _Root,
            Diff = DiffIndex.Parse(string.Empty),
            ChangedFileManifest = [new ChangedFile("script.sh", ChangedFileType.Edit)],
        };

    private static AutoFixFindingsStage Stage(
        AutoFixOptions options,
        IFixVerifier? verifier = null,
        IChatClient? chat = null,
        Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor>? editorFactory = null,
        Func<string, string[]>? lineReader = null)
    {
        var registry = new FindingFixerRegistry(
            [new BashUnquotedVarsFixer(), new BashSetEMissingFixer(), new DockerAddToCopyFixer()]);
        var agent = new NativeReviewAgent(new FakeChatClientFactory(chat ?? new ScriptedChatClient()));
        return new AutoFixFindingsStage(
            registry, agent, verifier ?? NullFixVerifier.Instance, options,
            NullLogger<AutoFixFindingsStage>.Instance, lineReader, editorFactory);
    }

    private static AutoFixOptions Options(
        bool enabled = true,
        string[]? authors = null,
        string[]? rules = null,
        int maxFixes = 3,
        bool commands = false)
        => new()
        {
            Enabled = enabled,
            AllowedAuthors = authors ?? ["creator-1"],
            AllowedRuleIds = rules ?? ["bash.unquoted-vars"],
            MaxFixesPerRun = maxFixes,
            EnableThreadFixCommands = commands,
        };

    private sealed class RecordingEditor : HashLineEditor
    {
        public int ApplyRangeCalls;
        public int WriteCalls;

        public RecordingEditor(RepoPathGuard guard, IReadOnlySet<string> writable)
            : base(guard, writable)
        {
        }

        public override EditResult ApplyRange(
            string relativePath, int startLine, int endLine, string replacement, string expectedRangeHash)
        {
            ApplyRangeCalls++;
            return base.ApplyRange(relativePath, startLine, endLine, replacement, expectedRangeHash);
        }

        public override void WriteAllLines(string relativePath, string[] lines)
        {
            WriteCalls++;
            base.WriteAllLines(relativePath, lines);
        }
    }
    private sealed class FailingEditor(
        RepoPathGuard guard,
        IReadOnlySet<string> writable)
        : HashLineEditor(guard, writable)
    {
        public override EditResult ApplyRange(
            string relativePath, int startLine, int endLine, string replacement, string expectedRangeHash)
            => new(false, "synthetic apply failure", [], null);
    }

    [Fact]
    public void Stage_exposes_expected_name_and_order()
    {
        var stage = Stage(Options());

        Assert.Equal("auto-fix-findings", stage.Name);
        Assert.Equal(72, stage.Order);
    }

    [Fact]
    public void Options_expose_verification_command()
    {
        var options = new AutoFixOptions { VerificationCommand = "dotnet test" };

        Assert.Equal("dotnet test", options.VerificationCommand);
    }

    private sealed class CorruptingRestoreEditor(
        RepoPathGuard guard,
        IReadOnlySet<string> writable,
        Action corrupt)
        : HashLineEditor(guard, writable)
    {
        public override string[] ReadAllLines(string relativePath)
        {
            var lines = base.ReadAllLines(relativePath);
            corrupt();
            return lines;
        }
    }

    private sealed class StubVerifier(bool pass, bool requiresWrites, Action<string, string>? onVerify = null)
        : IFixVerifier
    {
        public List<string> VerifiedFiles { get; } = [];
        public string Name => "stub";
        public bool RequiresWorkspaceWrites => requiresWrites;

        public Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
        {
            VerifiedFiles.Add(relativeFilePath);
            onVerify?.Invoke(repoDir, relativeFilePath);
            return Task.FromResult(new FixVerdict(pass, pass ? "ok" : "verify failed"));
        }
    }

    private sealed class CancellingVerifier : IFixVerifier
    {
        public string Name => "cancelling";
        public bool RequiresWorkspaceWrites => true;

        public Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
            => throw new OperationCanceledException(ct);
    }

    private sealed class SequenceVerifier(params bool[] outcomes) : IFixVerifier
    {
        private readonly Queue<bool> _Outcomes = new(outcomes);

        public string Name => "sequence";
        public bool RequiresWorkspaceWrites => true;

        public Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
        {
            var passed = _Outcomes.Dequeue();
            return Task.FromResult(new FixVerdict(passed, passed ? "ok" : "verify failed"));
        }
    }

    // ---- gate 1: enabled + author allowlist ----

    [Fact]
    public async Task Disabled_options_are_a_no_op()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(enabled: false)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Null(ctx.AcceptedFindings[0].AppliedFix);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Non_allowlisted_author_gets_no_fixes()
    {
        WriteFile("script.sh", "echo $name");
        var ctx = Ctx(new PullRequest(
            1, "t", null, "head-sha", "base", "url", false, "intruder-9", "Mallory"));
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(authors: ["creator-1"])).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Null(ctx.AcceptedFindings[0].AppliedFix);
    }

    [Fact]
    public async Task Deterministic_allowlist_can_match_display_name()
    {
        WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(authors: ["pr author"])).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Empty_author_allowlist_disables_even_when_enabled()
    {
        WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(authors: [])).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Unknown_allowed_rule_id_matches_nothing()
    {
        WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(rules: ["docker.add-vs-copy"])).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Empty_registered_rule_intersection_still_processes_commands()
    {
        var ctx = CommandCtx("echo $name");
        var chat = EditScriptThenDone(1, "echo \"$name\"");

        await Stage(Options(commands: true, rules: []), chat: chat)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes);
    }

    // ---- deterministic pass ----

    [Fact]
    public async Task Allowlisted_finding_gets_fix_and_zero_writes()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        RecordingEditor? recording = null;
        var factoryInvoked = false;
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(
                Options(),
                editorFactory: (guard, writable) =>
                {
                    factoryInvoked = true;
                    recording = new RecordingEditor(guard, writable);
                    return recording;
                })
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.Equal("k1", fix.DedupeKey);
        Assert.Equal(FixOrigin.Deterministic, fix.Proposal.Origin);
        Assert.Equal("echo \"$name\"", fix.Proposal.Replacement);
        Assert.Same(fix, ctx.AcceptedFindings[0].AppliedFix);
        // Null verifier: the deterministic path never constructs an editor and never writes.
        Assert.False(factoryInvoked);
        Assert.Null(recording);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Fixer_decline_leaves_plain_finding()
    {
        var abs = WriteFile("script.sh", "echo \"$name\""); // already quoted → decline
        var before = File.ReadAllBytes(abs);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Null(ctx.AcceptedFindings[0].AppliedFix);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Skips_findings_without_key_anchor_or_with_downgraded_anchor()
    {
        WriteFile("script.sh", "echo $name");
        var noKey = Finding("bash.unquoted-vars", "script.sh", 1, "k") with {DedupeKey = null};
        var downgraded = Finding("bash.unquoted-vars", "script.sh", 1, "k2");
        downgraded.AnchorDowngraded = true;
        var ctx = Ctx();
        ctx.AcceptedFindings = [noKey, downgraded];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Missing_file_is_skipped_without_failing_the_run()
    {
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "ghost.sh", 1, "k1")];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Process_verifier_applies_verifies_and_reverts()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        string? contentAtVerify = null;
        var verifier = new StubVerifier(pass: true, requiresWrites: true,
            onVerify: (dir, rel) => contentAtVerify = File.ReadAllText(Path.Combine(dir, rel)));
        RecordingEditor? recording = null;
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(
                Options(), verifier,
                editorFactory: (guard, writable) => recording = new RecordingEditor(guard, writable))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes);
        Assert.Equal("stub", ctx.AppliedFixes[0].VerifierName);
        Assert.Equal("script.sh", verifier.VerifiedFiles.Single());
        // The fix WAS on disk at verification time, and the file is byte-identical after revert.
        Assert.Contains("echo \"$name\"", contentAtVerify);
        Assert.Equal(before, File.ReadAllBytes(abs));
        Assert.NotNull(recording);
        Assert.True(recording!.ApplyRangeCalls >= 1);
        Assert.Equal(0, recording.WriteCalls);
    }

    [Fact]
    public async Task Corrupting_editor_restore_fails_safety_check_before_publish()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Stage(
                    Options(),
                    new StubVerifier(pass: true, requiresWrites: true),
                    editorFactory: (guard, writable) =>
                        new CorruptingRestoreEditor(
                            guard, writable, () => File.WriteAllText(abs, "corrupted")))
                .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("poisoning", ex.Message);
        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Cancellation_during_verification_reverts_bytes_and_propagates()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Stage(Options(), new CancellingVerifier()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Deterministic_revert_preserves_file_without_trailing_newline()
    {
        var abs = WriteFile("script.sh", "echo $name");
        File.WriteAllText(abs, "echo $name");
        var before = File.ReadAllBytes(abs);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(), new StubVerifier(pass: true, requiresWrites: true))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Dropped_deterministic_file_refunds_budget_for_command()
    {
        WriteFile("other.sh", "echo $x");
        var ctx = CommandCtx("echo $name");
        ctx.ChangedFileManifest =
        [
            new ChangedFile("script.sh", ChangedFileType.Edit),
            new ChangedFile("other.sh", ChangedFileType.Edit),
        ];
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "other.sh", 1, "other-key")];
        var before = File.ReadAllBytes(Path.Combine(_Root, "script.sh"));

        await Stage(
                Options(commands: true, maxFixes: 1),
                new SequenceVerifier(false, true),
                chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.Equal(FixOrigin.LlmCommanded, fix.Proposal.Origin);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_Root, "script.sh")));
    }

    [Fact]
    public async Task Verify_failure_drops_that_files_fixes()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(), new StubVerifier(pass: false, requiresWrites: true))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Null(ctx.AcceptedFindings[0].AppliedFix);
        Assert.Equal(before, File.ReadAllBytes(abs)); // reverted despite failure
    }

    [Fact]
    public async Task Two_fixes_in_one_file_apply_in_stable_order_and_revert()
    {
        var abs = WriteFile("script.sh", "echo $one", "echo $two");
        var before = File.ReadAllBytes(abs);
        var contentAtVerify = string.Empty;
        var verifier = new StubVerifier(pass: true, requiresWrites: true,
            onVerify: (_, rel) => contentAtVerify = File.ReadAllText(abs));
        var ctx = Ctx();
        ctx.AcceptedFindings =
        [
            Finding("bash.unquoted-vars", "script.sh", 1, "k1"),
            Finding("bash.unquoted-vars", "script.sh", 2, "k2"),
        ];

        await Stage(Options(maxFixes: 3), verifier).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, ctx.AppliedFixes.Count);
        Assert.Equal("echo \"$one\"\necho \"$two\"\n", contentAtVerify);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task MaxFixesPerRun_caps_deterministic_fixes()
    {
        WriteFile("script.sh", "echo $one", "echo $two", "echo $three");
        var ctx = Ctx();
        ctx.AcceptedFindings =
        [
            Finding("bash.unquoted-vars", "script.sh", 1, "k1"),
            Finding("bash.unquoted-vars", "script.sh", 2, "k2"),
            Finding("bash.unquoted-vars", "script.sh", 3, "k3"),
        ];

        await Stage(Options(maxFixes: 2)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, ctx.AppliedFixes.Count);
    }

    // ---- commanded pass ----

    private ReviewContext CommandCtx(string fileContent, int threadId = 42, string? instruction = null)
    {
        var abs = WriteFile("script.sh", fileContent);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var commandText = instruction is null ? "/rf fix" : $"/rf fix {instruction}";
        var ctx = Ctx();
        ctx.Threads =
        [
            new ReviewThread(threadId, null, ReviewThreadStatus.Active,
                [new ThreadComment("creator-1", "PR Author", false, commandText, t0)],
                new ThreadAnchor("script.sh", 1, 1)),
        ];
        return ctx;
    }

    private static ScriptedChatClient EditThenDone(
        string absPath,
        string relPath,
        int line,
        string replacement,
        string summary = "quoted the variable.")
    {
        var lines = File.ReadAllLines(absPath);
        var hash = HashLine.Of(lines[line - 1]);
        return new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "EditFile",
                new Dictionary<string, object?>
                {
                    ["path"] = relPath,
                    ["edits"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["fromHash"] = hash,
                            ["toHash"] = null,
                            ["fromLine"] = null,
                            ["toLine"] = null,
                            ["replacement"] = replacement,
                        },
                    },
                })),
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = summary})));
    }

    private ScriptedChatClient EditScriptThenDone(
        int line,
        string replacement,
        string summary = "quoted the variable.")
        => EditThenDone(
            Path.Combine(_Root, "script.sh"), "script.sh", line, replacement, summary);

    [Fact]
    public async Task Command_happy_path_captures_fix_and_reverts()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");
        var before = File.ReadAllBytes(abs);

        await Stage(Options(commands: true), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.Equal("thread-42", fix.DedupeKey);
        Assert.Equal(FixOrigin.LlmCommanded, fix.Proposal.Origin);
        Assert.Equal(42, fix.Proposal.SourceThreadId);
        Assert.Equal("script.sh", fix.Proposal.FilePath);
        Assert.Equal("echo \"$name\"", fix.Proposal.Replacement);
        Assert.Equal("quoted the variable.", fix.Proposal.Rationale);
        Assert.Empty(ctx.FixCommandReplies);
        Assert.Equal(before, File.ReadAllBytes(abs)); // reverted
    }

    [Fact]
    public async Task Command_revert_preserves_file_without_trailing_newline()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");
        File.WriteAllText(abs, "echo $name");
        var before = File.ReadAllBytes(abs);

        await Stage(Options(commands: true), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Command_out_of_diff_gets_polite_reply()
    {
        var ctx = CommandCtx("echo $name");
        ctx.ChangedFileManifest = [new ChangedFile("other.sh", ChangedFileType.Edit)];

        await Stage(Options(commands: true), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Equal(42, reply.ThreadId);
        Assert.Contains("isn't part of the current diff", reply.Text);
    }

    [Fact]
    public async Task Command_agent_decline_gets_polite_reply()
    {
        var ctx = CommandCtx("echo $name");
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "no safe fix"})));

        await Stage(Options(commands: true), chat: chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Contains("couldn't derive a safe fix", reply.Text);
    }

    [Fact]
    public async Task Closed_bot_thread_does_not_cover_a_command()
    {
        var ctx = CommandCtx("echo $name");
        ctx.Threads =
        [
            ..ctx.Threads,
            new ReviewThread(
                99, "existing-fix", ReviewThreadStatus.Closed,
                [new ThreadComment("reviewforge-bot", "reviewforge bot", true, "fixed", DateTimeOffset.UtcNow.AddMinutes(-2))],
                new ThreadAnchor("script.sh", 1, 1)),
        ];

        await Stage(Options(commands: true), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Command_dedupes_against_this_runs_deterministic_fix()
    {
        var ctx = CommandCtx("echo $name");
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(commands: true, maxFixes: 3), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes); // only the deterministic fix
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Contains("already posted", reply.Text);
    }

    [Fact]
    public async Task Command_budget_exhaustion_invites_requeue()
    {
        WriteFile("other.sh", "echo $x");
        var ctx = CommandCtx("echo $name");
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options(commands: true, maxFixes: 1), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AppliedFixes); // deterministic fix consumed the budget
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Contains("budget for this run is exhausted", reply.Text);
        Assert.Contains("Reply /rf fix again", reply.Text);
    }

    [Fact]
    public async Task Command_verifier_failure_reverts_and_replies()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");
        var before = File.ReadAllBytes(abs);

        await Stage(
                Options(commands: true),
                new StubVerifier(pass: false, requiresWrites: true),
                chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Contains("failed verification", reply.Text);
        Assert.DoesNotContain("verify failed", reply.Text);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Non_author_command_is_ignored_entirely()
    {
        WriteFile("script.sh", "echo $name");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ctx = Ctx();
        ctx.Threads =
        [
            new ReviewThread(42, null, ReviewThreadStatus.Active,
                [new ThreadComment("someone-else", "Reviewer", false, "/rf fix", t0)],
                new ThreadAnchor("script.sh", 1, 1)),
        ];

        await Stage(Options(commands: true)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Empty(ctx.FixCommandReplies);
    }

    [Theory]
    [InlineData("../sentinel")]
    [InlineData("")]
    public async Task Malformed_command_anchor_is_rejected_without_agent_call(string anchorPath)
    {
        WriteFile("script.sh", "echo $name");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ctx = Ctx();
        ctx.ChangedFileManifest = [new ChangedFile(anchorPath, ChangedFileType.Edit)];
        ctx.Threads =
        [
            new ReviewThread(42, null, ReviewThreadStatus.Active,
                [new ThreadComment("creator-1", "PR Author", false, "/rf fix", t0)],
                new ThreadAnchor(anchorPath, 1, 1)),
        ];
        var chat = new ScriptedChatClient();

        await Stage(Options(commands: true), chat: chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Commands_disabled_by_default()
    {
        var ctx = CommandCtx("echo $name");

        await Stage(Options(commands: false), chat: EditScriptThenDone(1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Empty(ctx.FixCommands);
    }
    [Fact]
    public async Task Failed_editor_application_skips_verification_and_drops_fixes()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        var verifier = new StubVerifier(pass: true, requiresWrites: true);
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(
                Options(),
                verifier,
                editorFactory: (guard, writable) => new FailingEditor(guard, writable))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Empty(verifier.VerifiedFiles);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Revert_cleans_temporary_file_when_restore_move_fails()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var verifier = new StubVerifier(
            pass: true,
            requiresWrites: true,
            onVerify: (_, _) =>
            {
                File.Delete(abs);
                Directory.CreateDirectory(abs);
            });
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Assert.ThrowsAnyAsync<Exception>(
            () => Stage(Options(), verifier).ExecuteAsync(ctx, CancellationToken.None));

        Assert.True(Directory.Exists(abs));
        Assert.Empty(Directory.EnumerateFiles(_Root, "*.revert", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Command_with_blank_summary_uses_default_rationale()
    {
        var ctx = CommandCtx("echo $name");

        await Stage(
                Options(commands: true),
                chat: EditScriptThenDone(1, "echo \"$name\"", summary: "   "))
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.Equal("addresses the review comment with a minimal edit", fix.Proposal.Rationale);
    }
}
