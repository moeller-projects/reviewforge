using System.Text.Json;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Per-run sink for everything the agent records: findings, uncertainties, the final
/// narrative. In-memory first (a crashed loop keeps what was recorded), optionally
/// mirrored to a findings.jsonl stream for process-crash durability.
/// </summary>
public sealed class ReviewCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented = false};

    private readonly List<RichFinding> _Findings = [];
    private readonly object _Gate = new();
    private readonly TextWriter? _Jsonl;
    private readonly HashSet<string> _KnownKeys;
    private readonly List<ReviewUncertainty> _Uncertainties = [];

    public ReviewCollector(IEnumerable<string>? knownDedupeKeys = null, TextWriter? jsonlSink = null)
    {
        _KnownKeys = new HashSet<string>(knownDedupeKeys ?? [], StringComparer.Ordinal);
        _Jsonl = jsonlSink;
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

            _Jsonl?.WriteLine(JsonSerializer.Serialize(finding, JsonOptions));
            _Jsonl?.Flush();
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