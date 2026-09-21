using System.Threading.Channels;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Service.Queue;

public sealed record ReviewRequest(Guid RunId, PrKey Pr, DateTimeOffset EnqueuedAt);

public sealed record EnqueueResult(bool Accepted, int QueueDepth);

public enum RunState
{
    Queued,
    Running,
    Completed,
    Skipped,
    Failed
}

public sealed record RunStatus(Guid RunId, PrKey Pr, RunState State, string? Detail, DateTimeOffset UpdatedAt);

/// <summary>Bounded ingest queue shared by the review workers.</summary>
public sealed class ReviewQueue
{
    private readonly int _Capacity;
    private readonly Channel<ReviewRequest> _Channel;

    public ReviewQueue(int capacity = 100)
    {
        _Capacity = capacity;
        _Channel = Channel.CreateBounded<ReviewRequest>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
        ReviewForgeTelemetry.Meter.CreateObservableGauge(
            "reviewforge.queue.depth", () => _Channel.Reader.Count);
    }

    public int Capacity => _Capacity;

    public int ApproximateDepth => _Channel.Reader.Count;

    public EnqueueResult TryEnqueue(ReviewRequest request)
    {
        var accepted = _Channel.Writer.TryWrite(request);
        var depth = _Channel.Reader.Count;
        if (!accepted)
        {
            ReviewForgeTelemetry.QueueRejected.Add(1);
        }

        return new EnqueueResult(accepted, depth);
    }

    public IAsyncEnumerable<ReviewRequest> ReadAllAsync(CancellationToken ct)
        => _Channel.Reader.ReadAllAsync(ct);

    public void Complete() => _Channel.Writer.Complete();
}

/// <summary>
/// In-memory run status for the status endpoint. Entries are retained for a bounded
/// period and count; status is still lost on host restart.
/// </summary>
public sealed class RunTracker(
    TimeProvider? clock = null,
    TimeSpan? retention = null,
    int maxEntries = 10_000)
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly object _Gate = new();
    private readonly int _MaxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));
    private readonly TimeSpan _Retention = retention ?? DefaultRetention;
    private readonly Dictionary<Guid, RunStatus> _Runs = [];

    public void Set(Guid runId, PrKey pr, RunState state, string? detail = null)
    {
        lock (_Gate)
        {
            var now = _Clock.GetUtcNow();
            _Runs[runId] = new RunStatus(runId, pr, state, detail, now);
            Evict(now);
        }
    }

    public RunStatus? Get(Guid runId)
    {
        lock (_Gate)
        {
            Evict(_Clock.GetUtcNow());
            return _Runs.TryGetValue(runId, out var status) ? status : null;
        }
    }

    private void Evict(DateTimeOffset now)
    {
        foreach (var runId in _Runs
                     .Where(pair => IsTerminal(pair.Value.State) && now - pair.Value.UpdatedAt > _Retention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _Runs.Remove(runId);
        }

        var terminalCount = _Runs.Values.Count(status => IsTerminal(status.State));
        foreach (var runId in _Runs.Values
                     .Where(status => IsTerminal(status.State))
                     .OrderBy(status => status.UpdatedAt)
                     .Take(Math.Max(0, terminalCount - _MaxEntries))
                     .Select(status => status.RunId)
                     .ToArray())
        {
            _Runs.Remove(runId);
        }
    }

    private static bool IsTerminal(RunState state)
        => state is RunState.Completed or RunState.Skipped or RunState.Failed;
}