using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service.Queue;
using Xunit;

namespace ReviewForge.Service.Tests;

/// <summary>Durable-queue (ReviewForge:QueueMode=Sqlite) behavior across host restarts: a
/// queued run survives, status reads through the queue row on a fresh tracker, the run
/// completes on a new host, and finalized status reads through the store afterwards.
/// Sequential hosts share one store/queue database file.</summary>
[Collection("ReviewForge service host")]
public sealed class DurableQueueRestartTests : IAsyncLifetime
{
    private readonly string _SharedDir = Path.Combine(Path.GetTempPath(), "rf-restart-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_SharedDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ReviewForge__QueueMode", null);
        if (Directory.Exists(_SharedDir))
        {
            Directory.Delete(_SharedDir, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Queued_run_survives_restart_and_status_reads_through_queue_and_store()
    {
        Guid runId;
        await using (var host1 = new RestartFactory(_SharedDir, withoutWorkers: true))
        {
            var submit = await host1.CreateClient().PostAsJsonAsync(
                "/reviews", new SubmitReviewRequest("o", "p", "repo", 5));
            Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
            runId = (await submit.Content.ReadFromJsonAsync<SubmitReviewResponse>())!.RunId;
        } // host1 disposed — the run was never started and never acked

        await using (var host2 = new RestartFactory(_SharedDir, withoutWorkers: true))
        {
            var status = await host2.CreateClient().GetFromJsonAsync<RunStatus>($"/reviews/{runId}");
            Assert.Equal(RunState.Queued, status!.State); // queue read-through on a fresh tracker
        }

        await using (var host3 = new RestartFactory(_SharedDir, withoutWorkers: false))
        {
            var client = host3.CreateClient();
            RunStatus? status = null;
            for (var i = 0; i < 400 && status?.State != RunState.Completed && status?.State != RunState.Failed; i++)
            {
                // 404 is a legitimate in-flight window: the worker's claim removed the
                // queue row and BeginRunStage has not yet persisted the run shell. Linux
                // CI never lands in it; Windows timing does.
                var response = await client.GetAsync($"/reviews/{runId}");
                status = response.IsSuccessStatusCode
                    ? await response.Content.ReadFromJsonAsync<RunStatus>()
                    : null;
                await Task.Delay(50);
            }

            Assert.Equal(RunState.Completed, status!.State); // the queued run completed on a new host
        }

        await using (var host4 = new RestartFactory(_SharedDir, withoutWorkers: true))
        {
            var status = await host4.CreateClient().GetFromJsonAsync<RunStatus>($"/reviews/{runId}");
            Assert.Equal(RunState.Completed, status!.State); // store read-through after tracker loss
        }
    }

    /// <summary>Host wired for restart scenarios: durable SQLite queue + real SQLite store on a
    /// shared database file; the fake ports (PR source, git, chat) stay in-memory per host.</summary>
    private sealed class RestartFactory : ReviewForgeFactory
    {
        private readonly string _SharedDir;

        public RestartFactory(string sharedDir, bool withoutWorkers)
        {
            _SharedDir = sharedDir;
            Environment.SetEnvironmentVariable("ReviewForge__WorkDir", sharedDir);
            Environment.SetEnvironmentVariable(
                "ReviewForge__StoreConnectionString",
                $"Data Source={Path.Combine(sharedDir, "restart.db")};Pooling=False");
            Environment.SetEnvironmentVariable("ReviewForge__QueueMode", "Sqlite");
            if (withoutWorkers)
            {
                WithoutWorkers();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var connectionString = $"Data Source={Path.Combine(_SharedDir, "restart.db")};Pooling=False";
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFindingStore>();
                services.AddSingleton<IFindingStore>(_ => new SqliteFindingStore(connectionString));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("ReviewForge__QueueMode", null);
            }
        }
    }
}
