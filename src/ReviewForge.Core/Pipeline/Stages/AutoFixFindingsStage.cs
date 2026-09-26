using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 7.2 (between validate and begin-run): produces suggestion-only fixes.
/// Pass 1 is deterministic — validated findings with a registered, eligible fixer get a
/// pure proposal that goes straight into the applied list (with the default null verifier
/// the deterministic path performs ZERO disk writes). Pass 2 is commanded — the PR author
/// replied "/rf fix" on a thread, and a constrained agent pass (one-file writable set,
/// hash-anchored edits, no findings tools) drafts the fix; its writes are always reverted
/// before the stage ends. The shared MaxFixesPerRun budget is consumed deterministic-first.
/// Never remove a fixed finding from AcceptedFindings — triage depends on its key staying
/// current (fixed findings must not trigger "no longer reproduces" auto-resolve).
///
/// <remarks>
/// Direct System.IO by design — same exception as RepoReadTools/ValidateFindingsStage
/// (see the IWorkspaceFs scope note). Reads via <paramref name="lineReader"/>; writes go
/// through <see cref="HashLineEditor"/> (guard + writable set) and are always reverted.
/// </remarks>
/// </summary>
public sealed class AutoFixFindingsStage : IReviewStage
{
    private readonly FindingFixerRegistry _Registry;
    private readonly NativeReviewAgent _Agent;
    private readonly IFixVerifier _Verifier;
    private readonly AutoFixOptions _Options;
    private readonly ILogger<AutoFixFindingsStage> _Logger;
    private readonly Func<string, string[]> _LineReader;
    private readonly Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor> _EditorFactory;

    public AutoFixFindingsStage(
        FindingFixerRegistry registry,
        NativeReviewAgent agent,
        IFixVerifier verifier,
        AutoFixOptions options,
        ILogger<AutoFixFindingsStage> logger,
        Func<string, string[]>? lineReader = null,
        Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor>? editorFactory = null)
    {
        _Registry = registry;
        _Agent = agent;
        _Verifier = verifier;
        _Options = options;
        _Logger = logger;
        _LineReader = lineReader ?? File.ReadAllLines;
        _EditorFactory = editorFactory ?? ((guard, writable) => new HashLineEditor(guard, writable));
    }

    public string Name => "auto-fix-findings";

    public int Order => 72;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (!_Options.Enabled)
        {
            return;
        }

        var pr = ctx.RequirePullRequest();
        if (_Options.AllowedAuthors.Length == 0)
        {
            _Logger.LogInformation("auto-fix: disabled — AutoFix:AllowedAuthors is empty");
            return;
        }

        if (!_Options.AllowedAuthors.Any(author =>
                Matches(author, pr.CreatorId) || Matches(author, pr.CreatorName)))
        {
            _Logger.LogInformation(
                "auto-fix: PR creator {CreatorId}/{CreatorName} is not in the author allowlist",
                pr.CreatorId, pr.CreatorName);
            return;
        }

        // Gate 2: allowed rule ids ∩ registered fixers (ordinal-insensitive config side).
        var eligible = _Options.AllowedRuleIds
            .Select(id => (Id: id, Fixer: _Registry.TryGet(id, out var f) ? f : null))
            .Where(p => p.Fixer is not null)
            .ToDictionary(p => p.Id, p => p.Fixer!, StringComparer.OrdinalIgnoreCase);
        if (eligible.Count == 0)
        {
            _Logger.LogInformation("auto-fix: no allowed rule ids have a registered fixer");
            return;
        }

        var repoDir = ctx.RequireRepoDir();
        var guard = new RepoPathGuard(repoDir);
        var applied = new List<AppliedFix>();
        var budget = _Options.MaxFixesPerRun;

        await RunDeterministicPassAsync(ctx, repoDir, guard, eligible, applied, () => budget, remaining => budget = remaining, ct)
            .ConfigureAwait(false);

        if (_Options.EnableThreadFixCommands)
        {
            await RunCommandedPassAsync(ctx, repoDir, guard, applied, () => budget, remaining => budget = remaining, ct)
                .ConfigureAwait(false);
        }

        ctx.AppliedFixes = applied;
    }

    private async Task RunDeterministicPassAsync(
        ReviewContext ctx,
        string repoDir,
        RepoPathGuard guard,
        IReadOnlyDictionary<string, IFindingFixer> eligible,
        List<AppliedFix> applied,
        Func<int> getBudget,
        Action<int> setBudget,
        CancellationToken ct)
    {
        // Per file: proposals collected in finding order; the process verifier applies
        // them all, verifies once, and reverts. Grouped so verification is per file.
        var proposalsByFile = new Dictionary<string, List<(RichFinding Finding, FixProposal Proposal)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in ctx.AcceptedFindings)
        {
            if (getBudget() <= 0)
            {
                break;
            }

            if (finding.DedupeKey is null || finding.Anchor is null || finding.AnchorDowngraded)
            {
                continue;
            }

            if (!eligible.TryGetValue(finding.RuleId, out var fixer))
            {
                continue;
            }

            var path = RepoPath.Normalize(finding.Anchor.FilePath);
            string[] lines;
            try
            {
                lines = _LineReader(Path.Combine(repoDir, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _Logger.LogWarning(ex, "auto-fix: could not read {Path} for rule {Rule}", path, finding.RuleId);
                continue;
            }

            var proposal = fixer.TryPropose(
                new FixContext(finding, path, lines, ctx.Diff ?? DiffIndex.Parse(string.Empty)));
            if (proposal is null)
            {
                _Logger.LogInformation("auto-fix: fixer for {Rule} declined finding {Key}", finding.RuleId, finding.DedupeKey);
                ReviewForgeTelemetry.FixesDeclined.Add(1, FixTags(FixOrigin.Deterministic, finding.RuleId));
                continue;
            }

            if (!proposalsByFile.TryGetValue(path, out var list))
            {
                list = [];
                proposalsByFile[path] = list;
            }

            list.Add((finding, proposal));
            setBudget(getBudget() - 1);
        }

        if (proposalsByFile.Count == 0)
        {
            return;
        }

        if (!_Verifier.RequiresWorkspaceWrites)
        {
            // Default path: zero disk writes — proposals go straight to the applied list.
            foreach (var (path, proposals) in proposalsByFile)
            {
                foreach (var (finding, proposal) in proposals)
                {
                    AttachFix(finding, applied, new AppliedFix(finding.DedupeKey!, proposal, _Verifier.Name), ctx);
                }
            }

            return;
        }

        // Process verifier: apply all of a file's fixes, verify once, revert, and drop the
        // file's fixes on failure (attribution trade-off: one invocation per file).
        foreach (var (path, proposals) in proposalsByFile)
        {
            ct.ThrowIfCancellationRequested();
            var abs = Path.Combine(repoDir, path.Replace('/', Path.DirectorySeparatorChar));
            var snapshotBytes = File.ReadAllBytes(abs);
            var snapshotLines = _LineReader(abs);
            var editor = _EditorFactory(guard, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path });
            var appliedAny = true;

            try
            {
                foreach (var (_, proposal) in proposals.OrderByDescending(p => p.Proposal.StartLine))
                {
                    var rangeHash = HashLine.Of(string.Join(
                        '\n', snapshotLines.Skip(proposal.StartLine - 1)
                            .Take(proposal.EndLine - proposal.StartLine + 1)));
                    var result = editor.ApplyRange(
                        path, proposal.StartLine, proposal.EndLine, proposal.Replacement, rangeHash);
                    if (!result.Success)
                    {
                        _Logger.LogWarning(
                            "auto-fix: could not apply fix to {Path}:{Start}-{End}: {Error}",
                            path, proposal.StartLine, proposal.EndLine, result.Error);
                        appliedAny = false;
                        break;
                    }
                }

                FixVerdict verdict;
                if (appliedAny)
                {
                    verdict = await _Verifier.VerifyAsync(repoDir, path, ct).ConfigureAwait(false);
                }
                else
                {
                    verdict = new FixVerdict(false, "a fix could not be applied cleanly");
                }

                if (!verdict.Passed)
                {
                    _Logger.LogWarning(
                        "auto-fix: verification failed for {Path} ({Verifier}): {Reason} — dropping {Count} fix(es)",
                        path, _Verifier.Name, verdict.Reason, proposals.Count);
                    foreach (var (finding, _) in proposals)
                    {
                        ReviewForgeTelemetry.FixesDeclined.Add(1, FixTags(FixOrigin.Deterministic, finding.RuleId));
                    }

                    continue;
                }

                foreach (var (finding, proposal) in proposals)
                {
                    AttachFix(finding, applied, new AppliedFix(finding.DedupeKey!, proposal, _Verifier.Name), ctx);
                }
            }
            finally
            {
                RevertFile(editor, repoDir, path, snapshotLines, snapshotBytes);
            }
        }
    }

    private async Task RunCommandedPassAsync(
        ReviewContext ctx,
        string repoDir,
        RepoPathGuard guard,
        List<AppliedFix> applied,
        Func<int> getBudget,
        Action<int> setBudget,
        CancellationToken ct)
    {
        var pr = ctx.RequirePullRequest();
        var commands = FixCommandDetector.Scan(
            ctx.Threads, pr.CreatorId, pr.CreatorName, ctx.PriorRun?.LastObservedCommentAt);
        ctx.FixCommands = commands;
        if (commands.Count == 0)
        {
            return;
        }

        var replies = new List<(int ThreadId, string Text)>();
        var changedFiles = ctx.ChangedFiles
            .Select(RepoPath.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var command in commands.OrderBy(c => c.ThreadId))
        {
            ct.ThrowIfCancellationRequested();
            var anchor = command.Anchor;
            var path = RepoPath.Normalize(anchor.FilePath);

            if (getBudget() <= 0)
            {
                replies.Add((command.ThreadId,
                    $"The fix budget for this run is exhausted ({_Options.MaxFixesPerRun} fixes). Reply /rf fix again to re-queue for the next run."));
                continue;
            }

            // Dedupe: our own live bot thread or this run's fixes already cover an
            // overlapping range on the same file → link, don't duplicate.
            var alreadyCovered =
                ctx.Threads.Any(t =>
                    t.DedupeKey is not null
                    && t.Anchor is { } a
                    && string.Equals(RepoPath.Normalize(a.FilePath), path, StringComparison.OrdinalIgnoreCase)
                    && RangesOverlap(a.StartLine, a.EndLine, anchor.StartLine, anchor.EndLine))
                || applied.Any(f =>
                    string.Equals(f.Proposal.FilePath, path, StringComparison.OrdinalIgnoreCase)
                    && RangesOverlap(f.Proposal.StartLine, f.Proposal.EndLine, anchor.StartLine, anchor.EndLine));
            if (alreadyCovered)
            {
                replies.Add((command.ThreadId,
                    $"A fix covering {path}:{anchor.StartLine}–{anchor.EndLine} is already posted — see the suggestion on this thread's file."));
                continue;
            }

            if (!changedFiles.Contains(path) || (ctx.Diff?.NonReviewableFiles.ContainsKey(path) ?? false))
            {
                replies.Add((command.ThreadId,
                    "this thread's file isn't part of the current diff — nothing to fix"));
                continue;
            }

            var abs = Path.Combine(repoDir, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                replies.Add((command.ThreadId,
                    "this thread's file isn't part of the current diff — nothing to fix"));
                continue;
            }

            var snapshotBytes = File.ReadAllBytes(abs);
            var snapshotLines = _LineReader(abs);
            var editor = _EditorFactory(guard, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path });
            try
            {
                var prompt = FixPromptBuilder.Build(command, path, anchor.StartLine, anchor.EndLine);
                var pass = await _Agent.RunWithEditToolsAsync(
                        prompt,
                        new ReviewCollector(),
                        ctx.ContextStore,
                        repoDir,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path },
                        _Options.FixPassMaxIterations,
                        ct)
                    .ConfigureAwait(false);
                var change = pass.Editor.GetSessionChange(path);
                _Logger.LogInformation(
                    "fix pass for thread {ThreadId}: input={Input} output={Output} edited={Edited}",
                    command.ThreadId, pass.InputTokens, pass.OutputTokens, change is not null);

                if (change is null)
                {
                    // Agent declined: nothing was edited (hash-anchored drift would have
                    // failed the edit); reply politely and move on.
                    replies.Add((command.ThreadId,
                        "I couldn't derive a safe fix for this — please clarify or adjust manually."));
                    continue;
                }

                if (_Verifier.RequiresWorkspaceWrites)
                {
                    var verdict = await _Verifier.VerifyAsync(repoDir, path, ct).ConfigureAwait(false);
                    if (!verdict.Passed)
                    {
                        _Logger.LogWarning(
                            "fix pass for thread {ThreadId}: verification failed ({Verifier}): {Reason}",
                            command.ThreadId, _Verifier.Name, verdict.Reason);
                        replies.Add((command.ThreadId,
                            $"the fix failed verification ({verdict.Reason}) — nothing was published"));
                        ReviewForgeTelemetry.FixesDeclined.Add(1, FixTags(FixOrigin.LlmCommanded, "thread-command"));
                        continue;
                    }
                }

                var rationale = OneSentence(pass.Result.Narrative.ReviewSummary);
                var proposal = new FixProposal(
                    path,
                    change.Value.StartLine,
                    Math.Max(change.Value.StartLine, change.Value.EndLine),
                    change.Value.Replacement,
                    rationale,
                    FixOrigin.LlmCommanded,
                    command.ThreadId);
                AttachFix(null, applied, new AppliedFix($"{AppliedFix.CommandKeyPrefix}{command.ThreadId}", proposal, _Verifier.Name), ctx);
                setBudget(getBudget() - 1);
            }
            finally
            {
                RevertFile(editor, repoDir, path, snapshotLines, snapshotBytes);
            }
        }

        ctx.FixCommandReplies = replies;
    }

    private void AttachFix(RichFinding? finding, List<AppliedFix> applied, AppliedFix fix, ReviewContext ctx)
    {
        if (finding is not null)
        {
            finding.AppliedFix = fix;
        }

        applied.Add(fix);
        var pr = ctx.RequirePullRequest();
        var ruleOrThread = fix.Proposal.SourceThreadId is { } threadId ? $"thread-{threadId}" : finding?.RuleId ?? "thread";
        _Logger.LogInformation(
            "auto-fix: {Origin} {RuleOrThread} on {Path}:{Start}-{End} for author {Author}",
            fix.Proposal.Origin, ruleOrThread, fix.Proposal.FilePath,
            fix.Proposal.StartLine, fix.Proposal.EndLine, pr.CreatorName);
        ReviewForgeTelemetry.FixesApplied.Add(1, FixTags(fix.Proposal.Origin, ruleOrThread));
    }

    /// <summary>Reverts the file to its pre-pass state; a failed revert fails the run
    /// (a dirty pooled checkout would poison later runs).</summary>
    private void RevertFile(
        HashLineEditor editor, string repoDir, string path, string[] snapshotLines, byte[] snapshotBytes)
    {
        editor.WriteAllLines(path, snapshotLines);
        var abs = Path.Combine(repoDir, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.ReadAllBytes(abs).SequenceEqual(snapshotBytes))
        {
            throw new InvalidOperationException(
                $"auto-fix revert failed for {path}: checkout is left dirty (pooled-checkout poisoning)");
        }
    }

    private static string OneSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "addresses the review comment with a minimal edit";
        }

        var trimmed = text.Trim();
        var dot = trimmed.IndexOf('.');
        var sentence = (dot >= 0 ? trimmed[..(dot + 1)] : trimmed).Trim();
        const int max = 300;
        return sentence.Length <= max ? sentence : sentence[..max] + "…";
    }

    private static bool RangesOverlap(int aStart, int aEnd, int bStart, int bEnd)
        => aStart <= bEnd && bStart <= aEnd;

    private static bool Matches(string allowlisted, string? value)
        => value is not null && string.Equals(allowlisted, value, StringComparison.OrdinalIgnoreCase);

    private static TagList FixTags(FixOrigin origin, string rule)
        => new() { {"origin", origin.ToString().ToLowerInvariant()}, {"rule", rule} };
}
