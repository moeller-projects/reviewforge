using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReviewForge.Core.Pipeline;

/// <summary>Single ActivitySource/Meter for the whole pipeline; the host wires them into OTel.</summary>
public static class ReviewForgeTelemetry
{
    public const string SourceName = "ReviewForge";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");

    // ---- Tag name conventions (trace spans; only low-cardinality subsets on metrics) ----
    public const string TagRunId = "reviewforge.run.id";
    public const string TagPrId = "reviewforge.pr.id";
    public const string TagRepoId = "reviewforge.repo.id";
    public const string TagOrg = "reviewforge.pr.org";
    public const string TagProject = "reviewforge.pr.project";
    public const string TagHeadSha = "reviewforge.pr.head_sha";
    public const string TagStage = "stage";
    public const string TagResult = "result"; // completed | skipped | failed
    public const string TagReason = "reason";
    public const string TagWarmed = "warmed"; // true when a discovery mirror warmup prefetched this head
    public const string TagKind = "kind"; // pooled | private (checkout metrics)
    public const string TagShards = "shards"; // shard count on sharded review run metrics
}