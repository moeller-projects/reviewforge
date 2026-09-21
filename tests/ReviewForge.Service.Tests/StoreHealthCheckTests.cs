using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
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
        var check = new StoreHealthCheck(new ThrowingStore());

        var result = await check.CheckHealthAsync(Ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class HealthyStore : IFindingStore
    {
        public Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct) => Task.FromResult<PriorRun?>(null);
        public Task SaveRunAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewRun>>([]);
    }

    private sealed class ThrowingStore : IFindingStore
    {
        public Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct) => throw new InvalidOperationException("db down");
        public Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct) => Task.FromResult<PriorRun?>(null);
        public Task SaveRunAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewRun>>([]);
    }
}