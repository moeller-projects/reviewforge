using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 7.1: adversarial verification of accepted findings on the Fast tier. ONE bounded
/// non-agent request per run (a second only to retry malformed output) — no tools, no
/// function invocation, no compaction. Fail-open by design: any verifier failure keeps the
/// findings; the verifier may only subtract, never add or rewrite.
/// </summary>
/// <remarks>
/// Direct System.IO by design — same exception as RepoReadTools/ValidateFindingsStage
/// (see the IWorkspaceFs scope note). File slices are read via <paramref name="lineReader"/>,
/// contained by <see cref="PathSafety.IsContainedReal"/>, and cleaned with
/// <see cref="PromptText.Clean"/> before crossing the prompt boundary.
/// </remarks>
public sealed class VerifyFindingsStage(
    IChatClientFactory chatClientFactory,
    VerifyFindingsOptions options,
    ILogger<VerifyFindingsStage>? logger = null,
    Func<string, string[]>? lineReader = null) : IReviewStage
{
    private readonly Func<string, string[]> _LineReader = lineReader ?? File.ReadAllLines;
    private readonly Dictionary<string, string[]?> _FileCache = new(StringComparer.Ordinal);

    public string Name => "verify-findings";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (!options.Enabled || ctx.Validation.AcceptedFindings.Count == 0)
        {
            logger?.LogDebug("finding verification skipped for {Pr}: enabled={Enabled}, accepted={AcceptedCount}",
                ctx.Pr, options.Enabled, ctx.Validation.AcceptedFindings.Count);
            return;
        }

        if (ctx.Reasoning.Result?.ReviewDepth == "trivial diff — no agent run")
        {
            logger?.LogDebug("finding verification skipped for {Pr}: trivial diff", ctx.Pr);
            return;
        }

        // Deterministic findings (homoglyph/*) need no model challenge.
        var candidates = ctx.Validation.AcceptedFindings
            .Where(f => !f.RuleId.StartsWith("homoglyph/", StringComparison.Ordinal))
            .OrderByDescending(SeverityRank) // critical/high verified first
            .Take(options.MaxFindings)
            .ToList();
        if (candidates.Count == 0)
        {
            logger?.LogDebug("finding verification skipped for {Pr}: no model candidates, accepted={AcceptedCount}",
                ctx.Pr, ctx.Validation.AcceptedFindings.Count);
            return;
        }

        var repoDir = ctx.RequireRepoDir();
        var prompt = FindingsVerifierPrompt.Build(
            candidates, anchor => Slice(repoDir, anchor, options.ContextLines), options.MaxPromptChars);
        logger?.LogDebug("starting finding verification for {Pr}: candidates={CandidateCount}, maxPromptChars={MaxPromptChars}",
            ctx.Pr, candidates.Count, options.MaxPromptChars);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        IReadOnlyDictionary<string, FindingsVerifierPrompt.Verdict> verdicts;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = new RetryingChatClient(chatClientFactory.Create(ChatTier.Fast), chatClientFactory.IsTransientFailure, logger);
            var response = await client.GetResponseAsync(
                FindingsVerifierPrompt.Messages(prompt), cancellationToken: timeout.Token).ConfigureAwait(false);
            verdicts = VerdictParser.Parse(response.Text) ?? await Retry(client, prompt, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FindingsTelemetry.FindingsVerifierFailures.Add(
                1, new TagList {{ReviewForgeTelemetry.TagReason, ex.GetType().Name}});
            logger?.LogWarning(ex, "findings verifier failed — keeping all findings (fail-open)");
            return; // host shutdown cancellation still throws
        }
        finally
        {
            FindingsTelemetry.FindingsVerifierDurationMilliseconds.Record(stopwatch.ElapsedMilliseconds);
        }

        var rejected = ctx.Validation.AcceptedFindings
            .Where(f => f.DedupeKey is { } k
                        && verdicts.TryGetValue(k, out var v)
                        && v.Rejected)
            .ToList();
        foreach (var finding in rejected)
        {
            FindingsTelemetry.FindingsVerifierRejected.Add(
                1, new TagList {{"rule", finding.RuleId}});
            logger?.LogInformation("finding {Key} rejected by verifier", finding.DedupeKey);
        }

        ctx.Validation = ctx.Validation with {AcceptedFindings = ctx.Validation.AcceptedFindings.Except(rejected).ToList()};
        logger?.LogDebug("finding verification completed for {Pr}: rejected={RejectedCount}, remaining={RemainingCount}",
            ctx.Pr, rejected.Count, ctx.Validation.AcceptedFindings.Count);
    }

    /// <summary>One retry with a JSON-only nudge; a second malformed reply fails open (all kept).</summary>
    private async Task<IReadOnlyDictionary<string, FindingsVerifierPrompt.Verdict>> Retry(
        IChatClient client, string prompt, CancellationToken ct)
    {
        var retry = await client.GetResponseAsync(
            FindingsVerifierPrompt.RetryMessages(prompt), cancellationToken: ct).ConfigureAwait(false);
        if (VerdictParser.Parse(retry.Text) is { } verdicts)
        {
            return verdicts;
        }

        FindingsTelemetry.FindingsVerifierFailures.Add(
            1, new TagList {{ReviewForgeTelemetry.TagReason, "unparseable"}});
        logger?.LogWarning("findings verifier returned malformed output twice — keeping all findings (fail-open)");
        return new Dictionary<string, FindingsVerifierPrompt.Verdict>(StringComparer.Ordinal);
    }

    /// <summary>Numbered file slice of ±<paramref name="contextLines"/> around the anchor,
    /// anchor lines marked "&gt;&gt;". Null when the file is missing, unreadable, or escapes
    /// the checkout — the finding is then verified from its claim alone.</summary>
    private string? Slice(string repoDir, FindingAnchor anchor, int contextLines)
    {
        var path = Path.GetFullPath(Path.Combine(repoDir, anchor.FilePath.Replace('/', Path.DirectorySeparatorChar)));
        // Real-path check: checkout-planted symlinks must not let the verifier read outside
        // the checkout (lexical containment alone is insufficient — see P1-10).
        if (!PathSafety.IsContainedReal(repoDir, path) || !File.Exists(path))
        {
            return null;
        }

        if (!_FileCache.TryGetValue(path, out var lines))
        {
            try
            {
                lines = _LineReader(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "could not read {Path} for findings verification", path);
                lines = null;
            }

            _FileCache[path] = lines;
        }

        if (lines is null)
        {
            return null;
        }

        var from = Math.Max(1, anchor.StartLine - contextLines);
        var to = Math.Min(lines.Length, anchor.EndLine + contextLines);
        if (from > to)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var lineNo = from; lineNo <= to; lineNo++)
        {
            var marker = lineNo >= anchor.StartLine && lineNo <= anchor.EndLine ? ">>" : "  ";
            sb.Append(marker).Append(' ').Append(lineNo).Append(": ").AppendLine(lines[lineNo - 1]);
        }

        return PromptText.Clean(sb.ToString());
    }

    private static int SeverityRank(RichFinding finding) => finding.Severity.ToLowerInvariant() switch
    {
        "critical" => 5,
        "high" => 4,
        "medium" => 3,
        "low" => 2,
        "info" => 1,
        _ => 0,
    };
}