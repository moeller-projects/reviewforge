using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Persists only the verified findings from a review draft. Draft runs have no
/// external writes, so a pre-publication shell is unnecessary; their rows must not carry
/// findings forward from the previous ordinary review.</summary>
public sealed class PersistDraftRunStage(
    IFindingStore store,
    TimeProvider? clock = null,
    ILogger<PersistDraftRunStage>? logger = null) : IReviewStage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "persist-draft-run";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var findings = ctx.Validation.AcceptedFindings
            .Where(f => f.DedupeKey is not null)
            .Select(f => new StoredFinding(
                f.DedupeKey!, f.RuleId, f.Severity, f.Title,
                f.Anchor?.FilePath, f.Anchor?.StartLine, null,
                FindingJson: JsonSerializer.Serialize(f, JsonOptions)))
            .ToArray();
        var lastObservedComment = ctx.Fetch.Threads.SelectMany(t => t.Comments)
            .Select(c => (DateTimeOffset?) c.PublishedAt).Max();
        var run = new ReviewRun(
            ctx.RunId, ctx.Pr, ctx.RequirePullRequest().SourceCommitSha, ctx.Classification.Kind,
            ctx.StartedAt, _Clock.GetUtcNow(), Success: true, findings,
            LastObservedCommentAt: lastObservedComment,
            Pipeline: RunKind.ReviewDraft.ToString());
        logger?.LogDebug("persisted review draft {RunId} with {FindingCount} verified findings", ctx.RunId, findings.Length);
        return store.SaveRunAsync(run, ct);
    }
}