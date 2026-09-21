using Microsoft.Extensions.Logging;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 5: optional structural enrichment (code-review graph). Fail-safe by contract
/// and by belt-and-suspenders catch — enrichment trouble never fails the pipeline.
/// </summary>
public sealed class EnrichContextStage(IContextEnricher? enricher, ILogger<EnrichContextStage> logger) : IReviewStage
{
    public const string ContextName = "crg";

    public string Name => "enrich-context";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (enricher is null)
        {
            return;
        }

        if (ctx.RepoDir is not { } repoDir)
        {
            logger.LogWarning("enrichment skipped: repository not prepared");
            return;
        }

        try
        {
            var payload = await enricher.EnrichAsync(repoDir, ctx.DiffText, ct);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                ctx.ContextStore.Put(ContextName, payload);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "context enrichment failed — continuing without it");
        }
    }
}