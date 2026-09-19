using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;
using ReviewForge.Core.Workspaces;
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
    public ReviewForgeFactory()
    {
        // Minimal-hosting config must be visible before Program.cs runs — env vars are.
        Environment.SetEnvironmentVariable("Ado__OrgUrl", "https://dev.azure.com/test");
        Environment.SetEnvironmentVariable("Ado__Project", "test");
        Environment.SetEnvironmentVariable("Reasoning__Provider", "openai");
        Environment.SetEnvironmentVariable("Reasoning__Model", "test-model");
        Environment.SetEnvironmentVariable("ReviewForge__WorkDir", WorkDir);
        Environment.SetEnvironmentVariable("ReviewForge__StoreConnectionString", $"Data Source={Path.Combine(WorkDir, "test.db")};Pooling=False");
    }

    public FakePullRequestSource Source { get; } = new();
    public FakeFindingStore Store { get; } = new();
    public FakeGitOps Git { get; } = new();

    public ScriptedChatClient Chat { get; } = new(
        ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "all good"})));

    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "reviewforge-svc-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(WorkDir);
        Git.RepoDir = WorkDir;

        builder.ConfigureServices(services =>
        {
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
                Source, Store, Git,
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
            foreach (var key in new[] {"Ado__OrgUrl", "Ado__Project", "Reasoning__Provider", "Reasoning__Model", "ReviewForge__WorkDir", "ReviewForge__StoreConnectionString"})
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
            _Factory.Source, _Factory.Store, failingGit,
            new RepoCheckoutPool(failingGit, standaloneWorkDir),
            new FakeChatClientFactory(_Factory.Chat),
            options,
            LoggerFactory.Create(b => { }));
        var worker = new ReviewWorker(queue, tracker, failingFactory, new InFlightClaims(),
            LoggerFactory.Create(b => { }).CreateLogger<ReviewWorker>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = worker.StartAsync(cts.Token);

        var pr = new PrKey("o", "p", "r", 44);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await queue.EnqueueAsync(new ReviewRequest(first, pr, DateTimeOffset.UtcNow), cts.Token);
        await queue.EnqueueAsync(new ReviewRequest(second, pr, DateTimeOffset.UtcNow), cts.Token);

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

    private sealed class ExplosiveGitOps(string repoDir) : FakeGitOps
    {
        public override string CloneOrOpen(string cloneUrl, string workDir, string? pat) => repoDir;

        public override string GetDiff(string repoPath, string baseSha, string headSha)
            => throw new InvalidOperationException("git exploded");
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
}