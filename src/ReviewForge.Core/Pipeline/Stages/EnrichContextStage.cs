using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 5: optional structural enrichment (code-review graph). Fail-safe by contract
/// and by belt-and-suspenders catch — enrichment trouble never fails the pipeline.
/// </summary>
public sealed class EnrichContextStage(IContextEnricher? enricher, ILogger<EnrichContextStage> logger) : IReviewStage
{
    public string Name => "enrich-context";



    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (enricher is null)
        {
            return;
        }

        if (ctx.Repository.RepoDir is not { } repoDir)
        {
            logger.LogWarning("enrichment skipped: repository not prepared");
            return;
        }

        try
        {
            // Stage 3 may already have the call in flight (started once RepoDir+DiffText
            // existed); awaiting it here preserves the exact fail-safe contract.
            var payload = await (ctx.PendingEnrichment ?? enricher.EnrichAsync(repoDir, ctx.Repository.DiffText, ct))
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                ctx.ContextStore.Put(enricher.Name, payload);
            }
        }
        catch (Exception ex)
        {
            ReviewTelemetry.EnrichmentFailures.Add(1,
                new TagList { { ReviewForgeTelemetry.TagReason, ex.GetType().Name } });
            logger.LogWarning(ex, "context enrichment failed — continuing without it");
        }
    }
}