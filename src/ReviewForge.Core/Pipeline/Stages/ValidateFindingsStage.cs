using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 7: second validation gate. Re-anchors findings against the checkout
/// (AnchorResolver) and the diff (DiffIndex). Findings outside the current PR diff
/// are rejected rather than published as general comments.
/// </summary>
public sealed class ValidateFindingsStage(
    ILogger<ValidateFindingsStage> logger,
    Func<string, string[]>? lineReader = null) : IReviewStage
{
    private readonly Dictionary<string, AnchorResolver.PreparedFile> _FileCache = new(StringComparer.Ordinal);
    private readonly Func<string, string[]> _LineReader = lineReader ?? File.ReadAllLines;

    public string Name => "validate-findings";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var repoDir = ctx.RequireRepoDir();
        var accepted = new List<RichFinding>();
        var changedFiles = ctx.ChangedFiles
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in ctx.Result!.Findings)
        {
            if (finding.Anchor is null)
            {
                logger.LogDebug("finding {Key} rejected because it has no changed-line anchor", finding.DedupeKey);
                ReviewForgeTelemetry.FindingsRejected.Add(1, new TagList { { ReviewForgeTelemetry.TagReason, "no-anchor" } });
                continue;
            }

            if (!TryReanchor(finding, repoDir))
            {
                logger.LogDebug("finding {Key} rejected because its anchor cannot be verified", finding.DedupeKey);
                ReviewForgeTelemetry.FindingsRejected.Add(1, new TagList { { ReviewForgeTelemetry.TagReason, "anchor-unverified" } });
                continue;
            }

            var path = Normalize(finding.Anchor.FilePath);
            if (!changedFiles.Contains(path) ||
                (ctx.Diff is not null && !ctx.Diff.Contains(path, finding.Anchor.StartLine)))
            {
                logger.LogInformation("finding {Key} rejected because its anchor is outside the current PR diff", finding.DedupeKey);
                ReviewForgeTelemetry.FindingsRejected.Add(1, new TagList { { ReviewForgeTelemetry.TagReason, "not-in-diff" } });
                continue;
            }

            ReviewForgeTelemetry.FindingsAccepted.Add(1);
            accepted.Add(finding);
        }

        ctx.AcceptedFindings = accepted;
        return Task.CompletedTask;
    }

    /// <summary>Re-anchors via snippet; returns false when the snippet is unverifiable.</summary>
    private bool TryReanchor(RichFinding finding, string repoDir)
    {
        var path = Path.GetFullPath(Path.Combine(repoDir, finding.Anchor!.FilePath.Replace('/', Path.DirectorySeparatorChar)));
        // Real-path check: checkout-planted symlinks must not let anchor validation read
        // outside the checkout (lexical containment alone is insufficient — see P1-10).
        if (!PathSafety.IsContainedReal(repoDir, path) || !File.Exists(path))
            return false;

        if (!_FileCache.TryGetValue(path, out var file))
        {
            try
            {
                file = new AnchorResolver.PreparedFile(_LineReader(path));
                _FileCache[path] = file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not read {Path} for anchor validation", path);
                return false;
            }
        }

        var (resolution, anchor) = AnchorResolver.Resolve(finding, file);
        switch (resolution)
        {
            case AnchorResolver.Resolution.Unverifiable:
                return false;
            case AnchorResolver.Resolution.WeakSnippet:
                // Keep the stated anchor for diff-membership validation, but never post inline.
                finding.AnchorDowngraded = true;
                logger.LogInformation("finding {Key} downgraded: snippet too unspecific to reanchor", finding.DedupeKey);
                break;
            case AnchorResolver.Resolution.Reanchored when anchor is not null:
                finding.Anchor = anchor;
                break;
        }

        return true;
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}