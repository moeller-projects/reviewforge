using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.AutoFix;

/// <summary>
/// Builds the finding rows persisted by begin-run/persist-run: accepted findings (with
/// their applied-fix JSON), carried-forward prior findings, and — at finalize time —
/// audit-only rows for commanded fixes. Commanded fixes use "thread-{id}" keys that are
/// never finding identities: they are excluded from carry-forward and known-key queries.
/// </summary>
internal static class AppliedFixPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented = false};

    /// <summary>Accepted findings + carry-forward, with deterministic-fix JSON attached.</summary>
    public static List<StoredFinding> BuildRows(ReviewContext ctx)
        => BuildRows(ctx, includeCommandedAuditRows: false, threadIdResolver: null);

    /// <summary>Finalize rows: adds commanded-fix audit rows and resolves posted thread ids.</summary>
    public static List<StoredFinding> BuildFinalRows(
        ReviewContext ctx, Func<string, int?> threadIdResolver)
        => BuildRows(ctx, includeCommandedAuditRows: true, threadIdResolver);

    private static List<StoredFinding> BuildRows(
        ReviewContext ctx, bool includeCommandedAuditRows, Func<string, int?>? threadIdResolver)
    {
        var fixJsonByKey = ctx.AppliedFixes
            .Where(a => a.Proposal.SourceThreadId is null)
            .ToDictionary(a => a.DedupeKey, a => JsonSerializer.Serialize(a, JsonOptions), StringComparer.Ordinal);

        var acceptedKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);
        var rows = ctx.AcceptedFindings
            .Select(f => new StoredFinding(
                f.DedupeKey!, f.RuleId, f.Severity, f.Title,
                f.Anchor?.FilePath, f.Anchor?.StartLine,
                threadIdResolver?.Invoke(f.DedupeKey!),
                f.DedupeKey is not null && fixJsonByKey.TryGetValue(f.DedupeKey, out var json) ? json : null))
            // Carry-forward from P0-1: prior keys remain known identities. Commanded-fix
            // audit rows ("thread-") are NOT identities — they must never re-enter the
            // known-key set or the carry-forward chain.
            .Concat((ctx.PriorRun?.Findings ?? [])
                .Where(p => !acceptedKeys.Contains(p.DedupeKey)
                            && !p.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal))
                .Select(p => p with {AppliedFixJson = null}))
            .ToList();

        if (includeCommandedAuditRows)
        {
            foreach (var fix in ctx.AppliedFixes.Where(a => a.Proposal.SourceThreadId is not null))
            {
                rows.Add(new StoredFinding(
                    fix.DedupeKey,
                    "thread-command",
                    "info",
                    $"Commanded fix for thread {fix.Proposal.SourceThreadId}",
                    fix.Proposal.FilePath,
                    fix.Proposal.StartLine,
                    threadIdResolver?.Invoke(fix.DedupeKey) ?? fix.Proposal.SourceThreadId,
                    JsonSerializer.Serialize(fix, JsonOptions)));
            }
        }

        return rows;
    }
}
