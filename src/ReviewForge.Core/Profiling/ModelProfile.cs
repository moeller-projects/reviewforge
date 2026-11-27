using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Profiling;

public enum ModelCallProfile { Unspecified, Review, Triage, Fix, Verifier }

/// <summary>Closed metadata contract: never extend with raw provider bags or text content.</summary>
public sealed record ModelCallRecord(Guid RunId, Guid ProfileId, Guid CallId, long Sequence,
    string Stage, ModelCallProfile Profile, string? Provider, string? Model,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, double? DurationMilliseconds,
    string Outcome, long? InputTokens, long? OutputTokens, long? TotalTokens,
    decimal? Cost = null, string? Currency = null, string? CostProvenance = null,
    int SchemaVersion = 1, string RetryCoverage = "sdk-internal-unavailable",
    long? CachedInputTokens = null, long? ReasoningTokens = null,
    Guid? ParentCallId = null, int? AttemptNumber = null,
    string CollectionCoverage = "observed-provider-calls", string CostCoverage = "provider-not-reported");

/// <summary>Nullable sums with reporting coverage: a partial sum is never presented as a full total.</summary>
public sealed record ModelTokenSummary(long? Tokens, long ReportedCalls, long ObservedCalls)
{
    public string Coverage => ReportedCalls == 0 ? "unavailable" : ReportedCalls == ObservedCalls ? "complete" : "partial";
}

public sealed record ModelUsageSummary(ModelTokenSummary InputTokens, ModelTokenSummary OutputTokens,
    ModelTokenSummary TotalTokens, ModelTokenSummary CachedInputTokens, ModelTokenSummary ReasoningTokens);

// Optional additions keep pre-summary and original run-only SummaryJson readable without backfilling.
public sealed record ModelProfileAggregate(long CompletedCalls, long FailedCalls, long CancelledCalls,
    long InterruptedCalls, double ObservedDurationMilliseconds, long? ProviderIncompleteCalls = null,
    ModelUsageSummary? Usage = null, IReadOnlyDictionary<string, ModelProfileAggregate>? Stages = null,
    long? ObservedCalls = null);

public sealed record ModelProfileSnapshot(Guid RunId, Guid ProfileId, long ObservedCalls, bool Truncated,
    IReadOnlyList<ModelCallRecord> Calls, int SchemaVersion = 1, ModelProfileAggregate? Aggregate = null, bool Incomplete = false);

/// <summary>One bounded, thread-safe collector per run; counts continue after detail truncation.</summary>
public sealed class ModelProfileCollector(Guid runId, int limit = 10_000, TimeProvider? clock = null)
{
    private readonly object _Gate = new();
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly List<ModelCallRecord> _Calls = [];
    private readonly int _Limit = Math.Clamp(limit, 0, 10_000);
    private long _Observed;
    private readonly SummaryCounter _Summary = new();
    private readonly Dictionary<string, SummaryCounter> _Stages = [];
    private Guid _ProfileId = Guid.NewGuid();
    private bool _Incomplete;
    public Guid ProfileId { get { lock (_Gate) return _ProfileId; } }

    /// <summary>Reclaimed durable requests reuse their stable profile identity. Lost pre-restart observations stay unavailable.</summary>
    public void Resume(Guid profileId)
    {
        lock (_Gate)
        {
            if (_ProfileId == profileId) return;
            _ProfileId = profileId;
            _Incomplete = true;
            for (var i = 0; i < _Calls.Count; i++) _Calls[i] = _Calls[i] with { ProfileId = profileId };
        }
    }

    public ModelCallLease Begin(string stage, ModelCallProfile profile, string? provider, string? model)
    {
        lock (_Gate)
        {
            var sequence = ++_Observed;
            var record = new ModelCallRecord(runId, ProfileId, Guid.NewGuid(), sequence, SafeStage(stage), Enum.IsDefined(profile) ? profile : ModelCallProfile.Unspecified,
                provider is "openai" or "openai-codex" ? provider : null, SafeModel(model),
                _Clock.GetUtcNow(), null, null, "interrupted", null, null, null);
            _Summary.Observed++;
            if (!_Stages.TryGetValue(record.Stage, out var stageSummary))
                _Stages.Add(record.Stage, stageSummary = new());
            stageSummary.Observed++;
            var index = _Calls.Count < _Limit ? _Calls.Count : -1;
            if (index >= 0) _Calls.Add(record);
            return new(this, record, index, _Clock.GetTimestamp());
        }
    }

    // Explicit supported identifiers only, never arbitrary configured strings or response model ids.
    private static string? SafeModel(string? model) => model is
        "gpt-5" or "gpt-5.1" or "gpt-5.2" or "gpt-5.3" or "gpt-5.4" or "gpt-5.5" or "gpt-5.6" or
        "gpt-5-mini" or "gpt-5-nano" or "gpt-5-codex" or "gpt-5.1-codex" or "gpt-5.2-codex" or
        "gpt-5.3-codex" or "gpt-4.1" or "gpt-4.1-mini" or "gpt-4o" or "o3" or "o4-mini" ? model : null;

    public static string SafeStage(string stage) => stage is
        "FetchPrContext" or "ReviewGate" or "BeginDraftRun" or "ResolveGate" or "PrepareRepository" or
        "ClassifyRun" or "EnrichContext" or "ExecuteReasoning" or "ValidateFindings" or "VerifyFindings" or
        "AutoFixFindings" or "BeginRun" or "CommitFixes" or "TriageThreads" or "PublishFindings" or
        "CollectComments" or "TriageComments" or "PlanFixes" or "ApplyFixes" or "VerifyBuild" or
        "PersistDraftRun" or "ResolveCommitPush" or "ReplyComments" or "PersistRun" ? stage : "unavailable";

    public ModelProfileSnapshot Snapshot()
    {
        lock (_Gate) return new(runId, ProfileId, _Observed, _Observed > _Calls.Count, _Calls.ToArray(), Aggregate:
            _Summary.Snapshot(_Stages.ToDictionary(p => p.Key, p => p.Value.Snapshot())), Incomplete: _Incomplete);
    }

    // Cardinality is bounded by SafeStage, never by caller-controlled stage names or retained details.
    private sealed class SummaryCounter
    {
        public long Observed;
        private long _Completed, _Failed, _Cancelled, _ProviderIncomplete;
        private double _Duration;
        private readonly long[] _Tokens = new long[5];
        private readonly long[] _Reported = new long[5];

        public void Complete(string outcome, UsageDetails? usage, double duration)
        {
            if (outcome == "completed") _Completed++;
            else if (outcome == "failed") _Failed++;
            else if (outcome == "cancelled") _Cancelled++;
            else if (outcome == "incomplete") _ProviderIncomplete++;
            _Duration += duration;
            Add(0, usage?.InputTokenCount);
            Add(1, usage?.OutputTokenCount);
            Add(2, usage?.TotalTokenCount);
            Add(3, usage?.CachedInputTokenCount);
            Add(4, usage?.ReasoningTokenCount);
        }

        private void Add(int index, long? value)
        {
            if (value is not >= 0) return;
            _Tokens[index] += value.Value;
            _Reported[index]++;
        }

        private ModelTokenSummary Token(int index) => new(_Reported[index] == 0 ? null : _Tokens[index], _Reported[index], Observed);
        public ModelProfileAggregate Snapshot(IReadOnlyDictionary<string, ModelProfileAggregate>? stages = null) =>
            new(_Completed, _Failed, _Cancelled, Observed - _Completed - _Failed - _Cancelled - _ProviderIncomplete,
                _Duration, _ProviderIncomplete, new(Token(0), Token(1), Token(2), Token(3), Token(4)), stages, Observed);
    }

    public sealed class ModelCallLease(ModelProfileCollector owner, ModelCallRecord record, int index, long timestamp)
    {
        private bool _Finished;
        public void Complete(string outcome, UsageDetails? usage)
        {
            lock (owner._Gate)
            {
                if (_Finished) return;
                _Finished = true;
                var duration = owner._Clock.GetElapsedTime(timestamp).TotalMilliseconds;
                owner._Summary.Complete(outcome, usage, duration);
                owner._Stages[record.Stage].Complete(outcome, usage, duration);
                if (index < 0) return;
                owner._Calls[index] = record with { ProfileId = owner._ProfileId, CompletedAt = owner._Clock.GetUtcNow(),
                    DurationMilliseconds = duration,
                    Outcome = outcome is "completed" or "cancelled" or "failed" or "incomplete" ? outcome : "interrupted",
                    CachedInputTokens = Nonnegative(usage?.CachedInputTokenCount), ReasoningTokens = Nonnegative(usage?.ReasoningTokenCount),
                    InputTokens = Nonnegative(usage?.InputTokenCount),
                    OutputTokens = Nonnegative(usage?.OutputTokenCount), TotalTokens = Nonnegative(usage?.TotalTokenCount) };
            }
        }
        private static long? Nonnegative(long? value) => value >= 0 ? value : null;
    }
}

/// <summary>Execution-context local immutable correlation. Scoped restoration prevents cross-run leakage.
/// Shared transports never own a mutable current-run field.</summary>
public static class ModelCallContext
{
    public sealed record Correlation(ModelProfileCollector Collector, string Stage, ModelCallProfile Profile);
    private static readonly AsyncLocal<Correlation?> Slot = new();
    public static Correlation? Current => Slot.Value;
    public static IDisposable Enter(ModelProfileCollector collector, string stage, ModelCallProfile profile = ModelCallProfile.Unspecified)
    {
        var previous = Slot.Value;
        Slot.Value = new(collector, stage, profile);
        return new Scope(previous);
    }
    private sealed class Scope(Correlation? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
