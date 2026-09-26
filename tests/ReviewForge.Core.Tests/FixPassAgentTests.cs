using Microsoft.Extensions.AI;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class FixPassAgentTests : IDisposable
{
    private readonly string _Root;

    public FixPassAgentTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-fixpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
        File.WriteAllLines(Path.Combine(_Root, "script.sh"), ["echo $name"]);
        File.WriteAllLines(Path.Combine(_Root, "other.sh"), ["echo $other"]);
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private NativeReviewAgent Agent(IChatClient chat)
        => new(new FakeChatClientFactory(chat));

    [Fact]
    public async Task Fix_pass_edits_only_the_writable_file_and_reports_the_session_change()
    {
        var hash = HashLine.Of("echo $name");
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "EditFile",
                new Dictionary<string, object?>
                {
                    ["path"] = "script.sh",
                    ["edits"] = new[] {new LineEdit(hash, null, null, null, "echo \"$name\"")},
                })),
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "quoted it."})));

        var result = await Agent(chat).RunWithEditToolsAsync(
            "fix it", new ReviewCollector(), new ContextStore(),
            _Root, new HashSet<string>(StringComparer.Ordinal) {"script.sh"},
            maxIterations: 5, CancellationToken.None);

        Assert.True(result.Result.Findings is []);
        Assert.Equal("quoted it.", result.Result.Narrative.ReviewSummary);
        var change = result.Editor.GetSessionChange("script.sh");
        Assert.NotNull(change);
        Assert.Equal(1, change.Value.StartLine);
        Assert.Equal("echo \"$name\"", change.Value.Replacement);
        // The fix pass writes through to the checkout; the caller reverts from its snapshot.
        Assert.Equal("echo \"$name\"", File.ReadAllLines(Path.Combine(_Root, "script.sh"))[0]);
    }

    [Fact]
    public async Task Fix_pass_refuses_edits_outside_the_writable_set()
    {
        var otherHash = HashLine.Of("echo $other");
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls((
                "EditFile",
                new Dictionary<string, object?>
                {
                    ["path"] = "other.sh",
                    ["edits"] = new[] {new LineEdit(otherHash, null, null, null, "echo \"$other\"")},
                })),
            ScriptedChatClient.FunctionCalls((
                "TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "no safe fix"})));

        var result = await Agent(chat).RunWithEditToolsAsync(
            "fix it", new ReviewCollector(), new ContextStore(),
            _Root, new HashSet<string>(StringComparer.Ordinal) {"script.sh"},
            maxIterations: 5, CancellationToken.None);

        // The refused edit produced no session change and no disk write.
        Assert.Null(result.Editor.GetSessionChange("other.sh"));
        Assert.Equal("echo $other", File.ReadAllLines(Path.Combine(_Root, "other.sh"))[0]);
    }
}

public class FixPromptBuilderTests
{
    [Fact]
    public void Build_mentions_the_constrained_toolset_single_file_and_task_done_contract()
    {
        var command = new FixCommand(42, new ThreadAnchor("src/A.cs", 4, 9),
            "use the constant", "please use the constant here");
        var prompt = FixPromptBuilder.Build(command, "src/A.cs", 4, 9);

        Assert.Contains("src/A.cs", prompt);
        Assert.Contains("4–9", prompt);
        Assert.Contains("please use the constant here", prompt);
        Assert.Contains("use the constant", prompt);
        Assert.Contains("ReadFileWithHashes and EditFile", prompt);
        Assert.Contains("only edit `src/A.cs`", prompt);
        Assert.Contains("TaskDone", prompt);
        Assert.Contains("one sentence", prompt);
        Assert.Contains("<pr-supplied-data>", prompt);
    }

    [Fact]
    public void Build_without_instruction_says_none()
    {
        var command = new FixCommand(42, new ThreadAnchor("a.sh", 1, 1), null, "q");
        var prompt = FixPromptBuilder.Build(command, "a.sh", 1, 1);
        Assert.Contains("instruction: none", prompt);
    }
}
