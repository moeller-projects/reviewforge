using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Profiling;

/// <summary>Closed metadata union. Logical calls and application attempts are not physical provider calls.</summary>
public sealed record ExecutionActivityRecord(Guid RunId, Guid ProfileId, Guid Id, long Sequence,
    string Kind, string Stage, ModelCallProfile Profile, Guid? ParentId, Guid? LogicalCallId,
    Guid? AttemptId, int? AttemptNumber, string? ToolName, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null, double? DurationMilliseconds = null, string Outcome = "interrupted",
    double? GovernorWaitMilliseconds = null, double? BackoffMilliseconds = null,
    string ParentCoverage = "observed", int SchemaVersion = 2);

public sealed record ExecutionActivitySummary(long Observed, long Completed, long Failed, long Cancelled,
    long Interrupted, long MissingParent, double DurationMilliseconds, double? GovernorWaitMilliseconds,
    double? BackoffMilliseconds);

public sealed partial class ModelProfileCollector
{
    private long _Sequence;
    private readonly List<ExecutionActivityRecord> _Activities = [];
    private readonly Dictionary<(string Stage, string Kind), ActivityCounter> _ActivityCounters = [];
    // Transient routing only, never serialized. Bounded independently of provider-supplied ids.
    private readonly Dictionary<(Guid? Invocation, string Dispatch), ToolParent> _ToolParents = [];
    private sealed record ToolParent(Guid Id, Guid? Logical, Guid? Attempt, int? Number, ModelCallProfile Profile);

    private void LinkTools(ModelCallRecord call, IEnumerable<AIContent> contents)
    {
        lock (_Gate)
            foreach (var tool in contents.OfType<FunctionCallContent>())
                if (_ToolParents.Count < 10_000)
                    _ToolParents[(ModelCallContext.Current?.InvocationId, tool.CallId)] = new(call.CallId, call.LogicalCallId, call.AttemptId, call.AttemptNumber, call.Profile);
    }

    public ExecutionActivityLease BeginActivity(string kind, string? toolName = null, string? dispatchId = null, int? attemptNumber = null, Guid? invocationId = null)
    {
        lock (_Gate)
        {
            var context = ModelCallContext.Current?.Collector == this ? ModelCallContext.Current : null;
            var stage = SafeStage(context?.Stage ?? "unavailable");
            kind = kind is "logical" or "attempt" or "tool" ? kind : "unavailable";
            ToolParent? toolParent = null;
            if (dispatchId is not null) _ToolParents.Remove((invocationId ?? context?.InvocationId, dispatchId), out toolParent);
            var logical = toolParent?.Logical ?? context?.LogicalCallId;
            var attempt = toolParent?.Attempt ?? context?.AttemptId;
            var parent = kind == "tool" ? toolParent?.Id : kind == "attempt" ? logical : null;
            var id = Guid.NewGuid();
            var profile = toolParent?.Profile ?? context?.Profile ?? ModelCallProfile.Unspecified;
            var record = new ExecutionActivityRecord(runId, _ProfileId, id, ++_Sequence,
                kind, stage, Enum.IsDefined(profile) ? profile : ModelCallProfile.Unspecified, parent, kind == "logical" ? id : logical,
                kind == "attempt" ? id : attempt, attemptNumber ?? toolParent?.Number ?? context?.AttemptNumber, kind == "tool" ? SafeTool(toolName) : null,
                _Clock.GetUtcNow(), ParentCoverage: kind == "logical" ? "root" : parent is null ? "missing-parent" : "observed");
            if (!_Stages.ContainsKey(stage)) _Stages.Add(stage, new());
            if (!_ActivityCounters.TryGetValue((stage, kind), out var counter)) _ActivityCounters[(stage, kind)] = counter = new();
            counter.Observed++;
            if (record.ParentCoverage == "missing-parent") counter.Missing++;
            var index = _Calls.Count + _Activities.Count < _Limit ? _Activities.Count : -1;
            if (index >= 0) _Activities.Add(record);
            return new(this, record, counter, index, _Clock.GetTimestamp());
        }
    }

    public static string SafeTool(string? name) => name is "ReadFile" or "List" or "Grep" or "repo_file_diff" or
        "FindReferences" or "ReadContext" or "GetRulebook" or "RecordFinding" or "RecordUncertainty" or
        "TaskDone" or "RecordVerdict" or "ReadFileWithHashes" or "EditFile" ? name : "unavailable";

    private Dictionary<string, ExecutionActivitySummary> ActivitySummaries(string? stage = null) =>
        _ActivityCounters.Where(p => stage is null || p.Key.Stage == stage).GroupBy(p => p.Key.Kind)
            .ToDictionary(g => g.Key, g => new ExecutionActivitySummary(g.Sum(p => p.Value.Observed),
                g.Sum(p => p.Value.Completed), g.Sum(p => p.Value.Failed), g.Sum(p => p.Value.Cancelled),
                g.Sum(p => p.Value.Observed - p.Value.Completed - p.Value.Failed - p.Value.Cancelled),
                g.Sum(p => p.Value.Missing), g.Sum(p => p.Value.Duration),
                g.Any(p => p.Value.Wait is not null) ? g.Sum(p => p.Value.Wait) : null,
                g.Any(p => p.Value.Backoff is not null) ? g.Sum(p => p.Value.Backoff) : null));

    internal sealed class ActivityCounter
    {
        public long Observed, Completed, Failed, Cancelled, Missing;
        public double Duration;
        public double? Wait, Backoff;
    }

    public sealed class ExecutionActivityLease
    {
        private readonly ModelProfileCollector _Owner;
        private readonly ExecutionActivityRecord _Record;
        private readonly ActivityCounter _Counter;
        private readonly int _Index;
        private readonly long _Timestamp;
        private bool _Finished;
        public Guid Id => _Record.Id;
        internal ExecutionActivityLease(ModelProfileCollector owner, ExecutionActivityRecord record, ActivityCounter counter, int index, long timestamp)
            => (_Owner, _Record, _Counter, _Index, _Timestamp) = (owner, record, counter, index, timestamp);
        public void Complete(string outcome)
        {
            lock (_Owner._Gate)
            {
                if (_Finished) return;
                _Finished = true;
                outcome = outcome is "completed" or "failed" or "cancelled" ? outcome : "interrupted";
                if (outcome == "completed") _Counter.Completed++;
                if (outcome == "failed") _Counter.Failed++;
                if (outcome == "cancelled") _Counter.Cancelled++;
                var duration = _Owner._Clock.GetElapsedTime(_Timestamp).TotalMilliseconds;
                _Counter.Duration += duration;
                if (_Index >= 0) _Owner._Activities[_Index] = _Owner._Activities[_Index] with {
                    CompletedAt = _Owner._Clock.GetUtcNow(), DurationMilliseconds = duration, Outcome = outcome };
            }
        }
        // Measured waits may be recorded after the attempt has failed. They are not provider latency.
        public void Wait(double milliseconds, bool backoff = false)
        {
            lock (_Owner._Gate)
            {
                if (backoff) _Counter.Backoff = (_Counter.Backoff ?? 0) + milliseconds;
                else _Counter.Wait = (_Counter.Wait ?? 0) + milliseconds;
                if (_Index < 0) return;
                var record = _Owner._Activities[_Index];
                _Owner._Activities[_Index] = backoff ? record with { BackoffMilliseconds = (record.BackoffMilliseconds ?? 0) + milliseconds }
                    : record with { GovernorWaitMilliseconds = (record.GovernorWaitMilliseconds ?? 0) + milliseconds };
            }
        }
    }
}

/// <summary>Delegates the original function, schema, arguments, cancellation and result unchanged.</summary>
public sealed class ProfiledAIFunction(AIFunction inner, Guid? invocationId = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = ModelCallContext.Current;
        var activity = context?.Collector.BeginActivity("tool", Name, FunctionInvokingChatClient.CurrentContext?.CallContent.CallId, invocationId: invocationId);
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            activity?.Complete("completed");
            return result;
        }
        catch (Exception ex)
        {
            activity?.Complete(ex is OperationCanceledException ? "cancelled" : "failed");
            throw;
        }
    }
}
