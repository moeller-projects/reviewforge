using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 5: optional structural enrichment (code-review graph). Fail-safe by contract
/// and by belt-and-suspenders catch — enrichment trouble never fails the pipeline.
/// </summary>
public sealed class EnrichContextStage(IContextEnricher? enricher, ILogger<EnrichContextStage>? logger = null) : IReviewStage
{
    public string Name => "enrich-context";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (enricher is null)
        {
            logger?.LogDebug("context enrichment skipped: no enricher configured");
            return;
        }

        if (ctx.Repository.RepoDir is not { } repoDir)
        {
            logger?.LogDebug("context enrichment skipped: repository not prepared");
            logger?.LogWarning("enrichment skipped: repository not prepared");
            return;
        }

        try
        {
            logger?.LogDebug("starting context enrichment for {Pr}: enricher={Enricher}, overlap={HasPendingEnrichment}",
                ctx.Pr, enricher.Name, ctx.Repository.PendingEnrichment is not null);
            var payload = await (ctx.Repository.PendingEnrichment ?? enricher.EnrichAsync(repoDir, ctx.Repository.DiffText, ct))
                .ConfigureAwait(false);
            logger?.LogDebug("context enrichment completed for {Pr}: hasPayload={HasPayload}", ctx.Pr, !string.IsNullOrWhiteSpace(payload));
            if (!string.IsNullOrWhiteSpace(payload))
            {
                ctx.Reasoning.ContextStore.Put(enricher.Name, payload);
            }
        }
        catch (Exception ex)
        {
            ReviewTelemetry.EnrichmentFailures.Add(1,
                new TagList {{ReviewForgeTelemetry.TagReason, ex.GetType().Name}});
            logger?.LogWarning(ex, "context enrichment failed — continuing without it");
        }
    }
}