using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service.Queue;
using ReviewForge.Service.Security;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

[CollectionDefinition("ReviewForge service host")]
public sealed class ReviewForgeServiceCollectionDefinition
{
}

/// <summary>In-process host with fake ports; the real worker drains the queue.</summary>
public sealed class ReviewForgeFactory : WebApplicationFactory<Program>
{
    private bool _WithoutWorkers;

    public ReviewForgeFactory()
    {
        // Minimal-hosting config must be visible before Program.cs runs — env vars are.
        Environment.SetEnvironmentVariable("Ado__OrgUrl", "https://dev.azure.com/test");
        Environment.SetEnvironmentVariable("Ado__Project", "test");
        Environment.SetEnvironmentVariable("Reasoning__Provider", "openai");
        Environment.SetEnvironmentVariable("Reasoning__Model", "test-model");
        Environment.SetEnvironmentVariable("ReviewForge__WorkDir", WorkDir);
        Environment.SetEnvironmentVariable("ReviewForge__WorkerCount", "2");
        Environment.SetEnvironmentVariable("ReviewForge__StoreConnectionString", $"Data Source={Path.Combine(WorkDir, "test.db")};Pooling=False");
        Environment.SetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable, "test-key-1,test-key-2");
    }

    public FakePullRequestSource Source { get; } = new();
    public FakeFindingStore Store { get; } = new();
    public FakeGitOps Git { get; } = new();

    public ScriptedChatClient Chat { get; } = new(
        ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "all good"})));

    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "reviewforge-svc-" + Guid.NewGuid().ToString("N"));

    public ReviewForgeFactory WithoutWorkers()
    {
        _WithoutWorkers = true;
        return this;
    }

    /// <summary>Clears configured API keys so the host boots without any — fail-closed or opt-out paths.</summary>
    public ReviewForgeFactory WithoutApiKeys()
    {
        Environment.SetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable, null);
        return this;
    }

    /// <summary>No keys + the explicit development opt-out, so /reviews stays reachable.</summary>
    public ReviewForgeFactory WithDevelopmentOptOut()
    {
        Environment.SetEnvironmentVariable(ApiKeyOptions.KeysEnvironmentVariable, null);
        Environment.SetEnvironmentVariable("Api__AllowUnauthenticatedForDevelopment", "true");
        return this;
    }

    /// <summary>Overrides the fixed-window submit limit for rate-limit tests.</summary>
    public ReviewForgeFactory WithSubmitLimit(int permitLimit, int windowSeconds)
    {
        Environment.SetEnvironmentVariable("Api__SubmitPermitLimit", permitLimit.ToString());
        Environment.SetEnvironmentVariable("Api__SubmitWindowSeconds", windowSeconds.ToString());
        return this;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "test-key-1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(WorkDir);
        Git.RepoDir = WorkDir;

        builder.ConfigureServices(services =>
        {
            if (_WithoutWorkers)
            {
                services.RemoveAll<IHostedService>();
            }

            services.RemoveAll<IPullRequestSource>();
            services.RemoveAll<IFindingStore>();
            services.RemoveAll<IGitOps>();
            services.RemoveAll<IChatClientFactory>();
            services.RemoveAll<ReviewPipelineFactory>();

            services.AddSingleton<IPullRequestSource>(Source);
            services.AddSingleton<IFindingStore>(Store);
            services.AddSingleton<IGitOps>(Git);
            services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory(Chat));
            services.AddSingleton(sp => new ReviewPipelineFactory(
                Source, Store,
                sp.GetRequiredService<RepoCheckoutPool>(),
                new FakeChatClientFactory(Chat),
                sp.GetRequiredService<IOptions<ReviewForgeServiceOptions>>(),
                sp.GetRequiredService<ILoggerFactory>()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var key in new[]
            {
                "Ado__OrgUrl", "Ado__Project", "Reasoning__Provider", "Reasoning__Model",
                "ReviewForge__WorkDir", "ReviewForge__WorkerCount", "ReviewForge__StoreConnectionString",
                ApiKeyOptions.KeysEnvironmentVariable, "Api__AllowUnauthenticatedForDevelopment",
                "Api__SubmitPermitLimit", "Api__SubmitWindowSeconds",
            })
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            if (Directory.Exists(WorkDir))
            {
                try
                {
                    Directory.Delete(WorkDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

[Collection("ReviewForge service host")]
public class ServiceTests : IAsyncLifetime
{
    private readonly ReviewForgeFactory _Factory = new();


    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<RunStatus> WaitForState(Guid runId, params RunState[] final)
    {
        var tracker = _Factory.Services.GetRequiredService<RunTracker>();
        for (var i = 0; i < 200; i++)
        {
            if (tracker.Get(runId) is { } status && final.Contains(status.State))
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"run {runId} did not reach {string.Join("/", final)}");
    }

    [Fact]
    public async Task Health_is_ok()
    {
        var response = await _Factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Submit_validates_input()
    {
        var response = await _Factory.CreateClient().PostAsJsonAsync("/reviews",
            new {org = "", project = "p", repositoryId = "r", prId = 0});
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submit_conflicts_when_review_already_in_flight()
    {
        var claims = _Factory.Services.GetRequiredService<InFlightClaims>();
        Assert.True(claims.TryClaim(new PrKey("o", "p", "r", 77), Guid.NewGuid(), out _));

        var response = await _Factory.CreateClient().PostAsJsonAsync("/reviews",
            new {org = "o", project = "p", repositoryId = "r", prId = 77});

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_run_is_404()
    {
        var response = await _Factory.CreateClient().GetAsync($"/reviews/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Full_run_flows_submit_to_completed_with_posts()
    {
        var client = _Factory.CreateClient();
        var submit = await client.PostAsJsonAsync("/reviews",
            new {org = "o", project = "p", repositoryId = "r", prId = 42});
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        var body = await submit.Content.ReadFromJsonAsync<SubmitReviewResponse>();
        var status = await WaitForState(body!.RunId, RunState.Completed, RunState.Failed, RunState.Skipped);

        Assert.Equal(RunState.Completed, status.State);
        Assert.Contains(_Factory.Source.GeneralComments, c => c.Contains("full review"));
        Assert.Single(_Factory.Store.Runs);

        var statusResponse = await client.GetAsync(body.StatusUrl);

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
    }

    [Fact]
    public async Task Different_heads_in_same_repo_complete_concurrently()
    {
        var firstPr = new PrKey("o", "p", "r", 51);
        var secondPr = new PrKey("o", "p", "r", 52);
        _Factory.Source.PullRequestsByKey[firstPr] = new PullRequest(51, "one", null, "head-one", "base", "url", false);
        _Factory.Source.PullRequestsByKey[secondPr] = new PullRequest(52, "two", null, "head-two", "base", "url", false);
        _Factory.Git.CloneDelay = TimeSpan.FromMilliseconds(150);
        try
        {
            var client = _Factory.CreateClient();
            var first = await client.PostAsJsonAsync("/reviews",
                new {org = "o", project = "p", repositoryId = "r", prId = 51});
            var second = await client.PostAsJsonAsync("/reviews",
                new {org = "o", project = "p", repositoryId = "r", prId = 52});
            var firstBody = await first.Content.ReadFromJsonAsync<SubmitReviewResponse>();
            var secondBody = await second.Content.ReadFromJsonAsync<SubmitReviewResponse>();

            var statuses = await Task.WhenAll(
                WaitForState(firstBody!.RunId, RunState.Completed),
                WaitForState(secondBody!.RunId, RunState.Completed));

            Assert.All(statuses, status => Assert.Equal(RunState.Completed, status.State));
            Assert.True(_Factory.Git.MaxConcurrentClones > 1);
        }
        finally
        {
            _Factory.Source.PullRequestsByKey.Clear();
            _Factory.Git.CloneDelay = TimeSpan.Zero;
        }
    }


    [Fact]
    public async Task Draft_pr_is_skipped()
    {
        _Factory.Source.Pr = _Factory.Source.Pr with {IsDraft = true};
        try
        {
            var client = _Factory.CreateClient();
            var submit = await client.PostAsJsonAsync("/reviews",
                new {org = "o", project = "p", repositoryId = "r", prId = 43});
            var body = await submit.Content.ReadFromJsonAsync<SubmitReviewResponse>();

            var status = await WaitForState(body!.RunId, RunState.Skipped, RunState.Failed);
            Assert.Equal(RunState.Skipped, status.State);
            Assert.Equal("PR is a draft", status.Detail);
        }
        finally
        {
            _Factory.Source.Pr = _Factory.Source.Pr with {IsDraft = false};
        }
    }

    [Fact]
    public async Task Failed_run_marks_status_and_worker_keeps_draining()
    {
        // Standalone worker with its own queue — no race with the hosted worker.
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var standaloneWorkDir = Path.Combine(Path.GetTempPath(), "reviewforge-failing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(standaloneWorkDir);
        var options = Options.Create(new ReviewForgeServiceOptions {WorkDir = standaloneWorkDir});
        var failingGit = new ExplosiveGitOps(standaloneWorkDir);
        var failingFactory = new ReviewPipelineFactory(
            _Factory.Source, _Factory.Store,
            new RepoCheckoutPool(failingGit, new FakeWorkspaceFs(), standaloneWorkDir),
            new FakeChatClientFactory(_Factory.Chat),
            options,
            LoggerFactory.Create(b => { }));
        var worker = new ReviewWorker(queue, tracker, failingFactory, new InFlightClaims(),
            _Factory.Store, LoggerFactory.Create(b => { }).CreateLogger<ReviewWorker>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = worker.StartAsync(cts.Token);

        var pr = new PrKey("o", "p", "r", 44);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(queue.TryEnqueue(new ReviewRequest(first, pr, DateTimeOffset.UtcNow)).Accepted);
        Assert.True(queue.TryEnqueue(new ReviewRequest(second, pr, DateTimeOffset.UtcNow)).Accepted);

        for (var i = 0; i < 200 && tracker.Get(second)?.State != RunState.Failed; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(RunState.Failed, tracker.Get(first)?.State);
        Assert.Contains("git exploded", tracker.Get(first)!.Detail);
        Assert.Equal(RunState.Failed, tracker.Get(second)?.State); // poison message did not kill the worker

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            Directory.Delete(standaloneWorkDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string CheckoutDir(string workDir, string repositoryId, string headSha)
    {
        static string KeyComponent(string id)
        {
            var readable = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12].ToLowerInvariant();
            return $"{readable}-{hash}";
        }

        return Path.Combine(workDir, "checkouts", KeyComponent(repositoryId), KeyComponent(headSha));
    }

    [Fact]
    public async Task Second_run_on_same_pr_does_not_resolve_or_repost()
    {
        var pr = new PrKey("o", "p", "r", 42);
        _Factory.Source.ChangedFiles = [new ChangedFile("src/A.cs", ChangedFileType.Edit)];
        _Factory.Git.Diff = "+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n";

        // Pre-seed the checkout so the finding's anchor verifies against a real file.
        var checkoutDir = CheckoutDir(_Factory.WorkDir, "r", "head-sha");
        Directory.CreateDirectory(Path.Combine(checkoutDir, "src"));
        File.WriteAllLines(Path.Combine(checkoutDir, "src", "A.cs"), ["line one", "bad code here", "line three"]);

        static Dictionary<string, object?> FindingArgs() => new()
        {
            ["ruleId"] = "general.other",
            ["title"] = "bad code",
            ["severity"] = "high",
            ["category"] = "bug",
            ["description"] = "bad code found",
            ["snippet"] = "bad code here",
            ["filePath"] = "src/A.cs",
            ["startLine"] = 2,
        };

        // Run 1: record finding K then finish.
        _Factory.Chat.Reset(
            ScriptedChatClient.FunctionCalls(("RecordFinding", FindingArgs())),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})));

        var client = _Factory.CreateClient();
        var submit1 = await client.PostAsJsonAsync("/reviews", new {org = "o", project = "p", repositoryId = "r", prId = 42});
        var body1 = await submit1.Content.ReadFromJsonAsync<SubmitReviewResponse>();
        await WaitForState(body1!.RunId, RunState.Completed);

        var run1 = Assert.Single(_Factory.Store.Runs);
        var finding1 = Assert.Single(run1.Findings);
        var key = finding1.DedupeKey;
        var threadId = finding1.ThreadId!.Value;
        Assert.Single(_Factory.Source.PostedFindings);

        // Wait for the worker to release the claim before re-submitting the same PR.
        var claims = _Factory.Services.GetRequiredService<InFlightClaims>();
        for (var i = 0; i < 200 && claims.IsHeldBy(pr, body1.RunId); i++)
        {
            await Task.Delay(25);
        }

        // Simulate the live ADO thread plus a new human comment (so run 2 clears the gate as FollowUp).
        _Factory.Source.Threads.Add(new ReviewThread(threadId, key, ReviewThreadStatus.Active,
        [
            new ThreadComment("bot", "bot", true, "finding", DateTimeOffset.UtcNow.AddMinutes(-2)),
            new ThreadComment("human", "author", false, "still failing?", DateTimeOffset.UtcNow),
        ]));
        _Factory.Store.LastRun = new PriorRun(pr, "head-sha", DateTimeOffset.UtcNow.AddMinutes(-1), [key],
            [new StoredFinding(key, "general.other", "high", "bad code", "src/A.cs", 2, threadId)]);

        // Run 2: re-record K (dedupe-rejected) and finish.
        _Factory.Chat.Reset(
            ScriptedChatClient.FunctionCalls(("RecordFinding", FindingArgs())),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})));

        var submit2 = await client.PostAsJsonAsync("/reviews", new {org = "o", project = "p", repositoryId = "r", prId = 42});
        var body2 = await submit2.Content.ReadFromJsonAsync<SubmitReviewResponse>();
        await WaitForState(body2!.RunId, RunState.Completed);

        Assert.Single(_Factory.Source.PostedFindings); // no duplicate thread
        Assert.DoesNotContain(_Factory.Source.StatusChanges, s => s.Status == ReviewThreadStatus.Fixed);
        Assert.Contains(_Factory.Store.Runs.Last().Findings, f => f.DedupeKey == key); // carried forward
    }

    [Fact]
    public async Task Rerun_after_partial_failure_posts_no_duplicates()
    {
        var pr = new PrKey("o", "p", "r", 42);
        _Factory.Source.ChangedFiles = [new ChangedFile("src/A.cs", ChangedFileType.Edit)];
        _Factory.Git.Diff = "+++ b/src/A.cs\n@@ -1,1 +2,2 @@\n+bad code here\n+worse code here\n";

        var checkoutDir = CheckoutDir(_Factory.WorkDir, "r", "head-sha");
        Directory.CreateDirectory(Path.Combine(checkoutDir, "src"));
        File.WriteAllLines(Path.Combine(checkoutDir, "src", "A.cs"), ["line one", "bad code here", "worse code here"]);

        static Dictionary<string, object?> Finding(string snippet, int line) => new()
        {
            ["ruleId"] = "general.other",
            ["title"] = "t",
            ["severity"] = "high",
            ["category"] = "bug",
            ["description"] = "d",
            ["snippet"] = snippet,
            ["filePath"] = "src/A.cs",
            ["startLine"] = line,
        };

        // Run 1: two findings; the second post throws mid-publish (partial failure).
        _Factory.Source.ThrowOnNthPost = 2;
        _Factory.Chat.Reset(
            ScriptedChatClient.FunctionCalls(
                ("RecordFinding", Finding("bad code here", 2)),
                ("RecordFinding", Finding("worse code here", 3))),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})));

        var client = _Factory.CreateClient();
        var submit1 = await client.PostAsJsonAsync("/reviews", new {org = "o", project = "p", repositoryId = "r", prId = 42});
        var body1 = await submit1.Content.ReadFromJsonAsync<SubmitReviewResponse>();
        await WaitForState(body1!.RunId, RunState.Failed);

        var posted = Assert.Single(_Factory.Source.PostedFindings); // exactly one landed before the fault
        var postedKey = posted.Finding.DedupeKey!;
        var postedThreadId = posted.ThreadId;

        var claims = _Factory.Services.GetRequiredService<InFlightClaims>();
        for (var i = 0; i < 200 && claims.IsHeldBy(pr, body1.RunId); i++)
        {
            await Task.Delay(25);
        }

        // The successfully-posted thread survives the failed run (ADO is the source of truth).
        _Factory.Source.Threads.Add(new ReviewThread(postedThreadId, postedKey, ReviewThreadStatus.Active,
            [new ThreadComment("bot", "bot", true, "finding", DateTimeOffset.UtcNow)]));

        // Run 2: same two findings; the already-posted one is suppressed, only the other posts.
        _Factory.Source.ThrowOnNthPost = null;
        _Factory.Chat.Reset(
            ScriptedChatClient.FunctionCalls(
                ("RecordFinding", Finding("bad code here", 2)),
                ("RecordFinding", Finding("worse code here", 3))),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})));

        var submit2 = await client.PostAsJsonAsync("/reviews", new {org = "o", project = "p", repositoryId = "r", prId = 42});
        var body2 = await submit2.Content.ReadFromJsonAsync<SubmitReviewResponse>();
        await WaitForState(body2!.RunId, RunState.Completed);

        // Exactly two distinct threads ever created — one per finding, zero duplicates.
        Assert.Equal(2, _Factory.Source.PostedFindings.Count);
        Assert.Equal(2, _Factory.Source.PostedFindings.Select(p => p.Finding.DedupeKey).Distinct().Count());
    }

    private sealed class ExplosiveGitOps(string repoDir) : FakeGitOps
    {
        public override Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct)
            => Task.FromResult(repoDir);

        public override Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct)
            => throw new InvalidOperationException("git exploded");
    }
}

[Collection("ReviewForge service host")]
public class QueueSaturationEndpointTests
{
    [Fact]
    public async Task Submit_returns_503_when_queue_is_full()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var queue = factory.Services.GetRequiredService<ReviewQueue>();

        for (var i = 0; i < queue.Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(new ReviewRequest(
                Guid.NewGuid(),
                new PrKey("o", "p", "r", i + 1),
                DateTimeOffset.UtcNow)).Accepted);
        }

        var response = await client.PostAsJsonAsync("/reviews",
            new {org = "o", project = "p", repositoryId = "r", prId = queue.Capacity + 1});

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

[Collection("ReviewForge service host")]
public class DiWiringTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Ado:OrgUrl"] = "https://dev.azure.com/test",
            ["Ado:Project"] = "test",
            ["Reasoning:Provider"] = "openai",
            ["Reasoning:Model"] = "m",
            ["ReviewForge:WorkDir"] = Path.Combine(Path.GetTempPath(), "rf-di-" + Guid.NewGuid().ToString("N")),
            ["ReviewForge:StoreConnectionString"] = $"Data Source={Path.Combine(Path.GetTempPath(), "rf-di-" + Guid.NewGuid().ToString("N") + ".db")}",
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void WithPat(Action action)
    {
        var previousPat = Environment.GetEnvironmentVariable("REVIEWFORGE_ADO_PAT");
        Environment.SetEnvironmentVariable("REVIEWFORGE_ADO_PAT", "test-pat");
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEWFORGE_ADO_PAT", previousPat);
        }
    }

    [Fact]
    public void AddReviewForge_registers_and_validates()
    {
        WithPat(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReviewForge(BuildConfig());
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<ReviewQueue>());
            Assert.NotNull(provider.GetRequiredService<RunTracker>());
            Assert.NotNull(provider.GetRequiredService<InFlightClaims>());
            Assert.NotNull(provider.GetRequiredService<TimeProvider>());
            Assert.NotNull(provider.GetRequiredService<IGitOps>());
            Assert.NotNull(provider.GetRequiredService<IChatClientFactory>());
            Assert.NotNull(provider.GetRequiredService<IFindingStore>());
            Assert.NotNull(provider.GetRequiredService<IPullRequestSource>());
            Assert.NotNull(provider.GetRequiredService<ReviewPipelineFactory>().Create());
        });
    }

    [Fact]
    public void ChatClientFactory_is_singleton_and_disposed_with_provider()
    {
        WithPat(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReviewForge(BuildConfig());
            using var provider = services.BuildServiceProvider();
            var a = provider.GetRequiredService<IChatClientFactory>();
            var b = provider.GetRequiredService<IChatClientFactory>();
            Assert.Same(a, b);
        });
    }

    [Fact]
    public void WorkerCount_registers_multiple_workers()
    {
        WithPat(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReviewForge(BuildConfig(new Dictionary<string, string?> {["ReviewForge:WorkerCount"] = "3"}));
            using var provider = services.BuildServiceProvider();

            Assert.Equal(3, provider.GetServices<IHostedService>().OfType<ReviewWorker>().Count());
        });
    }

    [Fact]
    public void ReasoningEffort_binds_from_config()
    {
        WithPat(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReviewForge(BuildConfig(new Dictionary<string, string?>
            {
                ["ReviewForge:ReasoningEffort"] = "High",
            }));
            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            Assert.Equal(ReasoningEffort.High, options.ReasoningEffort);
        });
    }

    [Fact]
    public void ReasoningEffort_defaults_to_null_when_absent()
    {
        WithPat(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddReviewForge(BuildConfig());
            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<ReviewForgeServiceOptions>>().Value;
            Assert.Null(options.ReasoningEffort);
        });
    }

    [Fact]
    public void Missing_sections_fail_fast()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddReviewForge(config));
    }

    [Fact]
    public void Invalid_clean_run_vote_fails_fast()
    {
        WithPat(() =>
        {
            var options = Options.Create(new ReviewForgeServiceOptions
            {
                WorkDir = Path.Combine(Path.GetTempPath(), "rf-clean-" + Guid.NewGuid().ToString("N")),
                CleanRunVote = "Bogus",
            });
            var factory = new ReviewPipelineFactory(
                new FakePullRequestSource(), new FakeFindingStore(),
                new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), Path.GetTempPath()),
                new FakeChatClientFactory(new ScriptedChatClient()),
                options,
                LoggerFactory.Create(_ => { }));

            Assert.Throws<InvalidOperationException>(() => factory.Create());
        });
    }
}

[Collection("ReviewForge service host")]
public class ApiDocsDisabledTests : IAsyncLifetime
{
    private readonly ReviewForgeFactory _Factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Docs_urls_are_404_when_disabled()
    {
        var client = _Factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/scalar/v1")).StatusCode);
    }
}

[Collection("ReviewForge service host")]
public class ApiDocsEnabledTests : IAsyncLifetime
{
    private readonly ReviewForgeFactory _Factory = new();

    // The env var is read by Program.cs when the host boots (lazy, on CreateClient) — set it in
    // the constructor and clear it on dispose, since env vars are process-wide.
    public ApiDocsEnabledTests()
    {
        Environment.SetEnvironmentVariable("ApiDocs__Enabled", "true");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        Environment.SetEnvironmentVariable("ApiDocs__Enabled", null);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Openapi_document_is_served_with_metadata()
    {
        var client = _Factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("/reviews", json);
        Assert.Contains("SubmitReviewRequest", json);
        Assert.Contains("reviewforge API", json);
    }

    [Fact]
    public async Task Scalar_ui_is_served()
    {
        var client = _Factory.CreateClient();
        var response = await client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Rejects_zero_worker_count_during_registration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ado:OrgUrl"] = "https://dev.azure.com/test",
                ["Ado:Project"] = "test",
                ["Reasoning:Provider"] = "openai",
                ["Reasoning:Model"] = "test-model",
                ["ReviewForge:WorkerCount"] = "0",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => services.AddReviewForge(configuration));
    }
}

[Collection("ReviewForge service host")]
public sealed class EndpointFailureTests
{
    [Fact]
    public async Task Queue_failure_releases_claim()
    {
        using var factory = new ReviewForgeFactory();
        factory.Services.GetRequiredService<ReviewQueue>().Complete();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/reviews",
            new {org = "o", project = "p", repositoryId = "closed", prId = 99});

        Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
        var claims = factory.Services.GetRequiredService<InFlightClaims>();
        Assert.True(claims.TryClaim(new PrKey("o", "p", "closed", 99), Guid.NewGuid(), out _));
    }
}