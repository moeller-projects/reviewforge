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

/// <summary>Stage 8 CommitOnHead materialization: deterministic drift-guarded apply and
/// the commanded pass's conditional revert.</summary>
public sealed class AutoFixStageCommitTests : IDisposable
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    private readonly string _Root;

    public AutoFixStageCommitTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-autofix-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private string WriteFile(string rel, params string[] lines)
    {
        var path = Path.Combine(_Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static RichFinding Finding(string rule, string path, int line, string key)
        => new()
        {
            RuleId = rule, Title = "t", Severity = "medium", Category = "bug", Description = "d",
            Anchor = new FindingAnchor(path, line, line), DedupeKey = key,
        };

    private ReviewContext Ctx()
        => new(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(
                1, "t", null, "head-sha", "base", "url", false, "creator-1", "PR Author"),
            RepoDir = _Root,
            Diff = DiffIndex.Parse(string.Empty),
            ChangedFileManifest = [new ChangedFile("script.sh", ChangedFileType.Edit)],
        };

    private ReviewContext CommandCtx(string fileContent, int threadId = 42)
    {
        WriteFile("script.sh", fileContent);
        var ctx = Ctx();
        ctx.Threads =
        [
            new ReviewThread(threadId, null, ReviewThreadStatus.Active,
                [new ThreadComment("creator-1", "PR Author", false, "/fixit", DateTimeOffset.UtcNow.AddMinutes(-5))],
                new ThreadAnchor("script.sh", 1, 1)),
        ];
        return ctx;
    }

    private static AutoFixFindingsStage Stage(
        AutoFixOptions options,
        IChatClient? chat = null,
        Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor>? editorFactory = null)
    {
        var registry = new FindingFixerRegistry([new BashUnquotedVarsFixer()]);
        var agent = new NativeReviewAgent(new FakeChatClientFactory(chat ?? new ScriptedChatClient()));
        return new AutoFixFindingsStage(
            registry, agent, options, NullLogger<AutoFixFindingsStage>.Instance,
            editorFactory: editorFactory);
    }

    private static AutoFixOptions Options(bool commands = false, string[]? authors = null)
        => new()
        {
            Enabled = true,
            AllowedAuthors = authors ?? ["creator-1"],
            AllowedRuleIds = ["bash.unquoted-vars"],
            PublishMode = AutoFixOptions.ModeCommitOnHead,
            CommitAuthorName = "reviewforge[bot]",
            CommitAuthorEmail = "reviewforge@example.com",
            EnableThreadFixCommands = commands,
        };

    /// <summary>Editor that fails ApplyRange a scripted number of times before delegating.</summary>
    private sealed class FlakyApplyEditor(
        RepoPathGuard guard,
        IReadOnlySet<string> writable,
        int failures,
        string error = "hash mismatch — re-read and retry")
        : HashLineEditor(guard, writable)
    {
        public int Calls { get; private set; }

        public override EditResult ApplyRange(
            string relativePath, int startLine, int endLine, string replacement, string expectedRangeHash)
        {
            Calls++;
            if (Calls <= failures)
            {
                return new EditResult(false, error, [], null);
            }

            return base.ApplyRange(relativePath, startLine, endLine, replacement, expectedRangeHash);
        }
    }

    private static ScriptedChatClient EditThenDone(string absPath, string relPath, int line, string replacement)
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
                            ["fromHash"] = hash, ["toHash"] = null,
                            ["fromLine"] = null, ["toLine"] = null,
                            ["replacement"] = replacement,
                        },
                    },
                })),
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "quoted the variable."})));
    }

    [Fact]
    public async Task Deterministic_apply_success_writes_the_private_checkout()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.True(fix.AppliedToTree);
        Assert.Null(fix.CommitSha); // stage 10 stamps it, not 8
        Assert.Equal("echo \"$name\"", File.ReadAllText(abs).TrimEnd('\n'));
        Assert.Same(fix, ctx.AcceptedFindings[0].AppliedFix);
    }

    [Fact]
    public async Task Hash_drift_retries_once_with_a_fresh_proposal_and_succeeds()
    {
        var abs = WriteFile("script.sh", "echo $name");
        FlakyApplyEditor? editor = null;
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(
                Options(),
                editorFactory: (guard, writable) =>
                {
                    editor = new FlakyApplyEditor(guard, writable, failures: 1);
                    return editor;
                })
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.True(fix.AppliedToTree);
        Assert.Equal(2, editor!.Calls); // first apply drifted, retry landed
        Assert.Equal("echo \"$name\"", File.ReadAllText(abs).TrimEnd('\n'));
    }

    [Fact]
    public async Task Persistent_drift_fails_the_run_instead_of_degrading_to_suggestion()
    {
        var abs = WriteFile("script.sh", "echo $name");
        var before = File.ReadAllBytes(abs);
        FlakyApplyEditor? editor = null;
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        // CommitOnHead never silently falls back from the requested commit/push to a
        // suggestion: a fix that cannot be materialized fails the stage (and the run).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(
                Options(),
                editorFactory: (guard, writable) =>
                {
                    editor = new FlakyApplyEditor(guard, writable, failures: int.MaxValue);
                    return editor;
                })
            .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("bash.unquoted-vars", ex.Message);
        Assert.Equal(2, editor!.Calls); // initial + exactly one retry
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Fix_on_a_file_outside_the_changed_manifest_degrades()
    {
        WriteFile("script.sh", "echo $name");
        var ctx = Ctx();
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];
        ctx.ChangedFileManifest = [new ChangedFile("other.sh", ChangedFileType.Edit)];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.False(fix.AppliedToTree);
    }

    [Fact]
    public async Task Unreadable_file_is_skipped_without_a_fix()
    {
        var ctx = Ctx(); // no file on disk at all
        ctx.AcceptedFindings = [Finding("bash.unquoted-vars", "script.sh", 1, "k1")];

        await Stage(Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
    }

    [Fact]
    public async Task Commanded_accepted_fix_keeps_its_edits()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");

        await Stage(Options(commands: true), chat: EditThenDone(abs, "script.sh", 1, "echo \"$name\""))
            .ExecuteAsync(ctx, CancellationToken.None);

        var fix = Assert.Single(ctx.AppliedFixes);
        Assert.True(fix.AppliedToTree);
        Assert.Equal("echo \"$name\"", File.ReadAllText(abs).TrimEnd('\n')); // NOT reverted
        Assert.Empty(ctx.FixCommandReplies); // 10 queues the "Fixed in" reply, not 8
    }

    [Fact]
    public async Task Commanded_declined_pass_still_reverts()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");
        var before = File.ReadAllBytes(abs);
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "no safe fix"})));

        await Stage(Options(commands: true), chat: chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        Assert.Single(ctx.FixCommandReplies);
        Assert.Equal(before, File.ReadAllBytes(abs));
    }

    [Fact]
    public async Task Commanded_pass_without_task_done_reverts_and_replies()
    {
        var ctx = CommandCtx("echo $name");
        var abs = Path.Combine(_Root, "script.sh");
        var before = File.ReadAllBytes(abs);
        var hash = HashLine.Of(File.ReadAllLines(abs)[0]);
        // The agent edits the file but the script ends before task_done: partial agent work
        // must never be kept for the commit.
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "EditFile",
                new Dictionary<string, object?>
                {
                    ["path"] = "script.sh",
                    ["edits"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["fromHash"] = hash, ["toHash"] = null,
                            ["fromLine"] = null, ["toLine"] = null,
                            ["replacement"] = "echo \"$name\"",
                        },
                    },
                })));

        await Stage(Options(commands: true), chat: chat).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Contains("did not complete", reply.Text);
        Assert.Equal(before, File.ReadAllBytes(abs)); // partial edit reverted
    }

    [Fact]
    public async Task Gate_rejected_command_gets_a_rejection_reply()
    {
        var ctx = CommandCtx("echo $name");

        await Stage(Options(commands: true, authors: ["someone-else"]), chat: new ScriptedChatClient())
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedFixes);
        var reply = Assert.Single(ctx.FixCommandReplies);
        Assert.Equal(42, reply.ThreadId);
        Assert.Contains("not enabled", reply.Text);
    }
}