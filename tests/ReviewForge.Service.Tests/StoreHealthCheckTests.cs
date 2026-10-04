using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class StoreHealthCheckTests
{
    private static readonly HealthCheckContext Ctx = new();

    [Fact]
    public async Task Reports_healthy_when_store_is_reachable()
    {
        var check = new StoreHealthCheck(new HealthyStore());

        var result = await check.CheckHealthAsync(Ctx);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_store_throws()
    {
        var check = new StoreHealthCheck(new FakeFindingStore
        {
            ThrowOnPing = new InvalidOperationException("db down"),
        });

        var result = await check.CheckHealthAsync(Ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class HealthyStore : IFindingStore
    {
        public Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct) => Task.FromResult<PriorRun?>(null);
        public Task<IReadOnlySet<long>> GetCommandedFixThreadIdsAsync(PrKey pr, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());
        public Task<ReviewRun?> GetRunAsync(Guid runId, CancellationToken ct) => Task.FromResult<ReviewRun?>(null);
        public Task SaveRunAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewRun>>([]);
        public Task<IReadOnlyList<ReviewRun>> GetStaleShellsAsync(DateTimeOffset olderThan, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewRun>>([]);
        public Task<int> PruneAsync(DateTimeOffset olderThan, int minRunsPerPr, CancellationToken ct) => Task.FromResult(0);
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SavePushedFixesAsync(PrKey pr, Guid runId, IReadOnlyList<PushedFix> fixes, CancellationToken ct) => Task.CompletedTask;
        public Task ConfirmPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PushedFix>> GetUnrepliedPushedFixesAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<PushedFix>>([]);
        public Task<ReviewRun?> GetLastCompletedResolveRunAsync(PrKey pr, CancellationToken ct) => Task.FromResult<ReviewRun?>(null);
        public Task<IReadOnlyList<ResolveAction>> GetResolveActionsAsync(PrKey pr, IReadOnlyCollection<int> threadIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ResolveAction>>([]);
        public Task SaveResolveActionsAsync(PrKey pr, Guid runId, IReadOnlyList<ResolveAction> actions, CancellationToken ct) => Task.CompletedTask;
        public Task MarkResolveActionRepliedAsync(int id, CancellationToken ct) => Task.CompletedTask;
        public Task MarkPushedFixRepliedAsync(int pushedFixId, CancellationToken ct) => Task.CompletedTask;
    }

}