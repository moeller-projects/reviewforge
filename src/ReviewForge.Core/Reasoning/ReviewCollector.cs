using System.Text.Json;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Per-run sink for everything the agent records: findings, uncertainties, the final
/// narrative. In-memory first (a crashed loop keeps what was recorded), optionally
/// mirrored to a per-run <c>findings/{runId}.jsonl</c> stream for process-crash durability.
/// </summary>
public sealed class ReviewCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented = false};

    private readonly List<RichFinding> _Findings = [];
    private readonly object _Gate = new();
    private readonly TextWriter? _Jsonl;
    private readonly HashSet<string> _KnownKeys;
    private readonly HashSet<string> _RedetectedKeys = new(StringComparer.Ordinal);
    private readonly List<ReviewUncertainty> _Uncertainties = [];
    private readonly Guid? _RunId;
    private readonly string? _HeadSha;
    private readonly TimeProvider _Clock;

    public ReviewCollector(
        IEnumerable<string>? knownDedupeKeys = null,
        TextWriter? jsonlSink = null,
        Guid? runId = null,
        string? headSha = null,
        TimeProvider? clock = null)
    {
        _KnownKeys = new HashSet<string>(knownDedupeKeys ?? [], StringComparer.Ordinal);
        _Jsonl = jsonlSink;
        _RunId = runId;
        _HeadSha = headSha;
        _Clock = clock ?? TimeProvider.System;
    }

    public bool Done { get; private set; }

    public ReviewNarrative? Narrative { get; private set; }

    public IReadOnlyList<RichFinding> Findings
    {
        get
        {
            lock (_Gate)
            {
                return [.. _Findings];
            }
        }
    }

    public IReadOnlyList<ReviewUncertainty> Uncertainties
    {
        get
        {
            lock (_Gate)
            {
                return [.. _Uncertainties];
            }
        }
    }

    /// <summary>True when the key was already recorded (this run or a prior one).</summary>
    public bool IsKnown(string dedupeKey)
    {
        lock (_Gate)
        {
            return _KnownKeys.Contains(dedupeKey);
        }
    }

    /// <summary>Keys the agent re-detected this run that were rejected as already known —
    /// positive evidence the finding still reproduces.</summary>
    public IReadOnlyCollection<string> RedetectedKeys
    {
        get
        {
            lock (_Gate)
            {
                return [.. _RedetectedKeys];
            }
        }
    }

    /// <summary>Records that a known key was re-detected (dedupe-rejected) this run.</summary>
    public void MarkRedetected(string dedupeKey)
    {
        lock (_Gate)
        {
            _RedetectedKeys.Add(dedupeKey);
        }
    }

    /// <summary>Appends a validated finding and streams it to the JSONL sink.</summary>
    public void AddFinding(RichFinding finding)
    {
        lock (_Gate)
        {
            _Findings.Add(finding);
            if (finding.DedupeKey is not null)
            {
                _KnownKeys.Add(finding.DedupeKey);
            }

            if (_Jsonl is not null)
            {
                var envelope = new FindingEnvelope(_RunId, _HeadSha, _Clock.GetUtcNow(), finding);
                _Jsonl.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
                _Jsonl.Flush();
            }
        }
    }

    public void AddUncertainty(ReviewUncertainty uncertainty)
    {
        lock (_Gate)
        {
            _Uncertainties.Add(uncertainty);
        }
    }

    /// <summary>Marks the loop complete and stores the final narrative. Idempotent.</summary>
    public void Complete(ReviewNarrative narrative)
    {
        lock (_Gate)
        {
            Narrative ??= narrative;
            Done = true;
        }
    }

    /// <summary>Builds the run result; narrative defaults to an empty one when the loop never called task_done.</summary>
    public ReviewResult ToResult(string reviewDepth, string? ruleBookVersion = null)
        => new()
        {
            Narrative = Narrative ?? new ReviewNarrative(),
            Findings = Findings,
            Uncertainties = Uncertainties,
            ReviewDepth = reviewDepth,
            RuleBookVersion = ruleBookVersion,
        };
}

/// <summary>One JSONL line: run metadata plus the recorded finding.</summary>
internal sealed record FindingEnvelope(Guid? RunId, string? HeadSha, DateTimeOffset RecordedAt, RichFinding Finding);