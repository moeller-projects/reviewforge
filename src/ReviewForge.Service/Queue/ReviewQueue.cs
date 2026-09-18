using System.Threading.Channels;
using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Queue;

public sealed record ReviewRequest(Guid RunId, PrKey Pr, DateTimeOffset EnqueuedAt);

public enum RunState
{
    Queued,
    Running,
    Completed,
    Skipped,
    Failed
}

public sealed record RunStatus(Guid RunId, PrKey Pr, RunState State, string? Detail, DateTimeOffset UpdatedAt);

/// <summary>Bounded ingest queue: one writer per request, single worker reader.</summary>
public sealed class ReviewQueue(int capacity = 100)
{
    private readonly Channel<ReviewRequest> _Channel = Channel.CreateBounded<ReviewRequest>(
        new BoundedChannelOptions(capacity) {FullMode = BoundedChannelFullMode.Wait});

    public ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct)
        => _Channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<ReviewRequest> ReadAllAsync(CancellationToken ct)
        => _Channel.Reader.ReadAllAsync(ct);

    public void Complete() => _Channel.Writer.Complete();
}

/// <summary>In-memory run status for the status endpoint. State survives until host restart.</summary>
public sealed class RunTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly object _Gate = new();
    private readonly Dictionary<Guid, RunStatus> _Runs = [];

    public void Set(Guid runId, PrKey pr, RunState state, string? detail = null)
    {
        lock (_Gate)
        {
            _Runs[runId] = new RunStatus(runId, pr, state, detail, _Clock.GetUtcNow());
        }
    }

    public RunStatus? Get(Guid runId)
    {
        lock (_Gate)
        {
            return _Runs.TryGetValue(runId, out var status) ? status : null;
        }
    }
}