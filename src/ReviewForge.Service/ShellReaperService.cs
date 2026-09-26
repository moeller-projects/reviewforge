using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>
/// One-shot startup pass (P1-12) finalizing stale in-flight shells: a run that crashed or
/// was killed between BeginRunStage and PersistRunStage left Success=false/CompletedAt=null,
/// which would otherwise read as neither success nor failure and mislead the discovery
/// sweep. Each stale shell is finalized as a normal failure record — finding rows are
/// preserved by the SaveRunAsync upsert — so discovery backoff applies an intentional,
/// bounded window from reaper time, and at most StaleShellMinutes elapses before a crashed
/// head can be re-reviewed.
/// </summary>
/// <remarks>
/// The reap lives in <see cref="ReapOnceAsync"/> rather than inline in ExecuteAsync so tests
/// can await it deterministically: .NET 10 BackgroundService.StartAsync schedules
/// ExecuteAsync via Task.Run(stoppingCts.Token, ...), and an immediate StopAsync cancels
/// that token before the thread pool runs the delegate — the reap would be silently
/// dropped (StopAsync suppresses the resulting cancellation). In the hosted path the
/// service starts long before shutdown, so the scheduling race cannot occur in production.
/// </remarks>
public sealed class ShellReaperService(
    IFindingStore store,
    IOptions<ReviewForgeServiceOptions> options,
    ILogger<ShellReaperService> logger,
    TimeProvider clock) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => ReapOnceAsync();

    internal async Task ReapOnceAsync()
    {
        var olderThan = clock.GetUtcNow() - TimeSpan.FromMinutes(options.Value.StaleShellMinutes);
        // CancellationToken.None: the reap must complete once started; a half-reaped shell
        // would leave the row as a shell again and be caught on the next boot anyway.
        var shells = await store.GetStaleShellsAsync(olderThan, CancellationToken.None).ConfigureAwait(false);
        foreach (var shell in shells)
        {
            var finalized = shell with
            {
                CompletedAt = clock.GetUtcNow(),
                Success = false,
                Findings = [],
            };
            // CancellationToken.None: the reap must complete once started; a half-reaped
            // shell would leave the row as a shell again and be caught on the next boot.
            await store.SaveRunAsync(finalized, CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(
                "reaped stale in-flight shell {RunId} for {Pr} (started {StartedAt}, head {HeadSha}): finalized as failure",
                shell.Id, shell.Pr, shell.StartedAt, shell.HeadSha);
        }
    }
}
