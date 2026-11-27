using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Telemetry;

namespace ReviewForge.Service;

public enum RunState
{
    Queued,
    Running,
    Completed,
    Skipped,
    Failed
}

public sealed record RunStatus(Guid RunId, PrKey Pr, RunState State, string? Detail, DateTimeOffset UpdatedAt, RunKind Kind = RunKind.Review)
{
    private readonly IReadOnlyList<string> _LoadedStages = Array.Empty<string>();
    public IReadOnlyList<string> LoadedStages
    {
        get => _LoadedStages;
        init => _LoadedStages = Array.AsReadOnly(value.ToArray());
    }
    public string? CurrentStage { get; init; }
    public string? HeadSha { get; init; }
    public DateTimeOffset? EnqueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public EnqueueTrigger? Trigger { get; init; }

    public int ProgressPercent
    {
        get
        {
            if (State == RunState.Completed) return 100;
            if (State == RunState.Queued || LoadedStages.Count == 0 || CurrentStage is null) return 0;
            for (var i = 0; i < LoadedStages.Count; i++)
                if (LoadedStages[i] == CurrentStage)
                    return (i + (State == RunState.Skipped ? 1 : 0)) * 100 / LoadedStages.Count;
            return 0;
        }
    }
}

/// <summary>How the ingest queue is backed. Memory (default, in-memory channel — queued runs
/// are lost on restart) or Sqlite (durable rows on the store's database file).</summary>
public enum QueueMode
{
    Memory,
    Sqlite,
}

/// <summary>Bounded ingest queue backed by an in-memory channel. Queued runs are lost on host
/// restart; queued-run status read-through dies with the process.</summary>
public sealed class ReviewQueue : IReviewQueue
{
    private readonly int _Capacity;
    private readonly Channel<ReviewRequest> _Channel;
    private readonly ConcurrentDictionary<Guid, ReviewRequest> _Pending = new();

    public ReviewQueue(int capacity = 100)
    {
        _Capacity = capacity;
        // FullMode is irrelevant: the only write path is TryWrite (enqueue returns 503
        // when full instead of blocking HTTP callers). Kept explicit to document that
        // blocking producers are not a supported mode.
        _Channel = Channel.CreateBounded<ReviewRequest>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public int Capacity => _Capacity;

    public int ApproximateDepth => _Channel.Reader.Count;

    public EnqueueResult TryEnqueue(ReviewRequest request)
    {
        // _Pending must be registered BEFORE the channel write: a fast worker can
        // dequeue, complete, and ack in the window between TryWrite and registration,
        // which would resurrect a finished run as "Queued" on status read-through.
        _Pending[request.RunId] = request;
        var accepted = _Channel.Writer.TryWrite(request);
        var depth = _Channel.Reader.Count;
        if (!accepted)
        {
            _Pending.TryRemove(request.RunId, out _);
            QueueTelemetry.QueueRejected.Add(1);
        }

        return new EnqueueResult(accepted, depth);
    }

    public async IAsyncEnumerable<ReviewRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var request in _Channel.Reader.ReadAllAsync(ct))
        {
            _Pending.TryRemove(request.RunId, out _);
            yield return request;
        }
    }

    public void Acknowledge(Guid runId) => _Pending.TryRemove(runId, out _);

    public bool RenewClaim(Guid runId) => true; // no durable lease; InFlightClaims renews itself

    public ReviewRequest? TryGetQueued(Guid runId)
        => _Pending.TryGetValue(runId, out var request) ? request : null;

    public Task<IReadOnlyList<QueueRunSnapshot>> GetPendingAsync(IReadOnlyCollection<PrKey> prs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var keys = prs.ToHashSet();
        return Task.FromResult<IReadOnlyList<QueueRunSnapshot>>(
            _Pending.Values.Where(r => keys.Contains(r.Pr)).Select(r => new QueueRunSnapshot(r, false)).ToArray());
    }

    public Task<IReadOnlyList<QueueRunSnapshot>> GetPendingHistoryAsync(int count, CancellationToken ct)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<QueueRunSnapshot>>(_Pending.Values
            .OrderByDescending(r => r.EnqueuedAt).ThenByDescending(r => r.RunId.ToString("D"), StringComparer.Ordinal)
            .Take(count).Select(r => new QueueRunSnapshot(r, false)).ToArray());
    }

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

    public void Set(Guid runId, PrKey pr, RunState state, string? detail = null, RunKind kind = RunKind.Review)
    {
        lock (_Gate)
        {
            // Terminal states are sticky: a late/stale writer (e.g. a Queued write racing a
            // fast worker) must never resurrect a finished run.
            if (_Runs.TryGetValue(runId, out var existing)
                && IsTerminal(existing.State)
                && !IsTerminal(state))
            {
                return;
            }

            var now = _Clock.GetUtcNow();
            if (existing?.State == RunState.Running && state == RunState.Queued) return;
            _Runs[runId] = (existing ?? new RunStatus(runId, pr, state, detail, now, kind)) with
            {
                State = state,
                Detail = detail,
                UpdatedAt = now,
                EnqueuedAt = existing?.EnqueuedAt ?? (state == RunState.Queued ? now : null),
                StartedAt = existing?.StartedAt ?? (state == RunState.Running ? now : null),
                CompletedAt = existing?.CompletedAt ?? (IsTerminal(state) ? now : null),
            };
            Evict(now);
        }
    }

    /// <summary>Registers queue metadata before publication; late registrations cannot overwrite live progress.</summary>
    public void Register(ReviewRequest request)
    {
        lock (_Gate)
        {
            if (_Runs.ContainsKey(request.RunId)) return;
            var now = _Clock.GetUtcNow();
            _Runs[request.RunId] = new RunStatus(request.RunId, request.Pr, RunState.Queued, null, now, request.Kind)
            {
                HeadSha = request.HeadSha,
                EnqueuedAt = request.EnqueuedAt,
                Trigger = request.Trigger,
            };
            Evict(now);
        }
    }

    public void DefinitionLoaded(Guid runId, IReadOnlyList<StageId> stages)
    {
        lock (_Gate)
        {
            if (!_Runs.TryGetValue(runId, out var status) || IsTerminal(status.State)
                || status.LoadedStages.Count != 0) return;
            var now = _Clock.GetUtcNow();
            _Runs[runId] = status with
            {
                State = RunState.Running,
                LoadedStages = stages.Select(id => id.ToString()).ToArray(),
                StartedAt = status.StartedAt ?? now,
                UpdatedAt = now,
            };
        }
    }

    public void StageStarting(Guid runId, StageId stage)
    {
        lock (_Gate)
        {
            if (!_Runs.TryGetValue(runId, out var status) || IsTerminal(status.State)) return;
            var stages = status.LoadedStages.ToList();
            var next = stages.IndexOf(stage.ToString());
            var current = status.CurrentStage is null ? -1 : stages.IndexOf(status.CurrentStage);
            if (next < 0 || next <= current) return;
            _Runs[runId] = status with { CurrentStage = stage.ToString(), UpdatedAt = _Clock.GetUtcNow() };
        }
    }

    /// <summary>Called only after successful context fetch, never inferred from PR inventory.</summary>
    public void CaptureHead(Guid runId, string headSha)
    {
        lock (_Gate)
        {
            if (!_Runs.TryGetValue(runId, out var status) || IsTerminal(status.State)) return;
            _Runs[runId] = status with { HeadSha = headSha, UpdatedAt = _Clock.GetUtcNow() };
        }
    }

    /// <summary>Removes an entry (enqueue rollback). Idempotent.</summary>
    public void Remove(Guid runId)
    {
        lock (_Gate)
        {
            _Runs.Remove(runId);
        }
    }

    /// <summary>Point-in-time copy of every tracked run (tests and diagnostics).</summary>
    public IReadOnlyList<RunStatus> Snapshot()
    {
        lock (_Gate)
        {
            Evict(_Clock.GetUtcNow());
            return [.. _Runs.Values];
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