using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 8 (between validate and begin-run): produces fixes.
/// Pass 1 is deterministic — validated findings with a registered, eligible fixer get a
/// pure proposal. Pass 2 is commanded — the PR author replied "/fixit" on a thread, and a
/// constrained agent pass (one-file writable set, hash-anchored edits, no findings tools)
/// drafts the fix. The shared MaxFixesPerRun budget is consumed deterministic-first.
/// Publication depends on AutoFix:PublishMode: "Suggestion" posts ADO suggestion blocks (the
/// author's accept-click is the verification step) and never leaves workspace writes behind;
/// "CommitOnHead" materializes accepted fixes into the run's PRIVATE checkout
/// (drift-guarded range apply for deterministic fixes; kept agent edits for commanded fixes)
/// and stage 10 commits and pushes them. Never remove a fixed finding from
/// AcceptedFindings — triage depends on its key staying current (fixed findings must not
/// trigger "no longer reproduces" auto-resolve).
///
/// <remarks>
/// Direct System.IO by design — same exception as RepoReadTools/ValidateFindingsStage
/// (see the IWorkspaceFs scope note). Reads via the injected line reader; writes go
/// through <see cref="HashLineEditor"/> (guard + writable set). Suggestion-mode writes are
/// always reverted; CommitOnHead keeps accepted edits and reverts declined/failed passes.
/// </remarks>
/// </summary>
public sealed class AutoFixFindingsStage : IReviewStage
{
    private readonly FindingFixerRegistry _Registry;
    private readonly NativeReviewAgent _Agent;
    private readonly AutoFixOptions _Options;
    private readonly ILogger<AutoFixFindingsStage> _Logger;
    private readonly Func<string, string[]> _LineReader;
    private readonly Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor> _EditorFactory;
    private readonly IFindingStore? _Store;

    public AutoFixFindingsStage(
        FindingFixerRegistry registry,
        NativeReviewAgent agent,
        AutoFixOptions options,
        ILogger<AutoFixFindingsStage> logger,
        Func<string, string[]>? lineReader = null,
        Func<RepoPathGuard, IReadOnlySet<string>, HashLineEditor>? editorFactory = null,
        IFindingStore? store = null)
    {
        _Registry = registry;
        _Agent = agent;
        _Options = options;
        _Logger = logger;
        _LineReader = lineReader ?? File.ReadAllLines;
        _EditorFactory = editorFactory ?? ((guard, writable) => new HashLineEditor(guard, writable));
        _Store = store;
    }

    public string Name => "auto-fix-findings";


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
            ReplyToRejectedCommands(ctx, "auto-fix is disabled on this service — the /fixit command was not run.");
            return;
        }

        // Gate 1 matches the immutable creator id only. ADO display names are
        // user-editable and non-unique — they are never a security input.
        if (!_Options.AllowedAuthors.Any(author => Matches(author, pr.CreatorId)))
        {
            _Logger.LogInformation(
                "auto-fix: PR creator {CreatorId}/{CreatorName} is not in the author allowlist",
                pr.CreatorId, pr.CreatorName);
            ReplyToRejectedCommands(ctx, "auto-fix is not enabled for this pull request author — the /fixit command was not run.");
            return;
        }

        // Gate 2: either all registered fixers or the configured allowlist, intersected with the registry.
        var ruleIds = _Options.AllowAllRules
            ? _Registry.RegisteredRuleIds
            : _Options.AllowedRuleIds;
        var eligible = ruleIds
            .Select(id => (Id: id, Fixer: _Registry.TryGet(id, out var f) ? f : null))
            .Where(p => p.Fixer is not null)
            .ToDictionary(p => p.Id, p => p.Fixer!, StringComparer.OrdinalIgnoreCase);
        if (eligible.Count == 0)
        {
            _Logger.LogInformation("auto-fix: no allowed rule ids have a registered fixer; deterministic pass skipped");
        }

        var repoDir = ctx.RequireRepoDir();
        var guard = new RepoPathGuard(repoDir);
        var applied = new List<AppliedFix>();
        var budget = _Options.MaxFixesPerRun;

        if (eligible.Count > 0)
        {
            await RunDeterministicPassAsync(
                    ctx, guard, eligible, applied,
                    () => budget, remaining => budget = remaining, ct)
                .ConfigureAwait(false);
        }

        if (_Options.EnableThreadFixCommands)
        {
            await RunCommandedPassAsync(ctx, repoDir, guard, applied, () => budget, remaining => budget = remaining, ct)
                .ConfigureAwait(false);
        }

        ctx.AutoFix = ctx.AutoFix with {AppliedFixes = applied};
    }

    /// <summary>Gate failure must not silently drop author commands: every scanned /fixit
    /// command gets a plain rejection reply on its thread (a failed gate is published as a
    /// comment, never dropped). No audit rows are written — the gate did no work — so the
    /// comment watermark alone keeps this exactly-once.</summary>
    private void ReplyToRejectedCommands(ReviewContext ctx, string reason)
    {
        if (!_Options.EnableThreadFixCommands)
        {
            return;
        }

        var commands = FixCommandDetector.Scan(
            ctx.Fetch.Threads, ctx.RequirePullRequest().CreatorId, ctx.Fetch.PriorRun?.LastObservedCommentAt);
        ctx.AutoFix = ctx.AutoFix with {FixCommands = commands};
        if (commands.Count == 0)
        {
            return;
        }

        ctx.AutoFix = ctx.AutoFix with
        {
            FixCommandReplies =
            [
                .. commands.OrderBy(c => c.ThreadId).Select(c => (c.ThreadId, reason)),
            ],
        };
    }

    private async Task RunDeterministicPassAsync(
        ReviewContext ctx,
        RepoPathGuard guard,
        IReadOnlyDictionary<string, IFindingFixer> eligible,
        List<AppliedFix> applied,
        Func<int> getBudget,
        Action<int> setBudget,
        CancellationToken ct)
    {
        // Per file: proposals collected in finding order. Suggestion mode publishes them
        // without workspace writes; CommitOnHead materializes them into the private checkout
        // (drift-guarded range apply) so stage 10 can commit the edits.
        var proposalsByFile = new Dictionary<string, List<(RichFinding Finding, FixProposal Proposal, string[] Lines)>>(RepoPath.PathComparer);
        var guardSkipped = 0;

        foreach (var finding in ctx.Validation.AcceptedFindings)
        {
            ct.ThrowIfCancellationRequested();
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
            var resolved = guard.Resolve(path, out var guardError);
            if (resolved is null)
            {
                guardSkipped++;
                _Logger.LogWarning(
                    "auto-fix deterministic: skipping {File} — {Reason}",
                    path, guardError);
                continue;
            }

            string[] lines;
            try
            {
                lines = _LineReader(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _Logger.LogWarning(ex, "auto-fix: could not read {Path} for rule {Rule}", path, finding.RuleId);
                continue;
            }

            var proposal = fixer.TryPropose(
                new FixContext(finding, path, lines, ctx.Repository.Diff ?? DiffIndex.Parse(string.Empty)));
            if (proposal is null)
            {
                _Logger.LogInformation("auto-fix: fixer for {Rule} declined finding {Key}", finding.RuleId, finding.DedupeKey);
                AutoFixTelemetry.FixesDeclined.Add(1, FixTags(FixOrigin.Deterministic, finding.RuleId));
                continue;
            }

            if (!proposalsByFile.TryGetValue(path, out var list))
            {
                list = [];
                proposalsByFile[path] = list;
            }

            list.Add((finding, proposal, lines));
            setBudget(getBudget() - 1);
        }

        if (guardSkipped > 0)
        {
            AutoFixTelemetry.DeterministicGuardSkipped.Add(guardSkipped);
            _Logger.LogInformation("auto-fix deterministic summary: guard_skipped={GuardSkipped}", guardSkipped);
        }

        if (proposalsByFile.Count == 0)
        {
            return;
        }

        if (_Options.IsCommitOnHead)
        {
            ApplyProposalsToTree(ctx, guard, eligible, proposalsByFile, applied);
            return;
        }

        foreach (var (_, proposals) in proposalsByFile)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (finding, proposal, _) in proposals)
            {
                ct.ThrowIfCancellationRequested();
                AttachFix(finding, applied, new AppliedFix(finding.DedupeKey!, proposal), ctx);
            }
        }
    }

    /// <summary>CommitOnHead deterministic materialization: applies each proposal to the
    /// private checkout via the editor's drift-guarded range API. Hash drift → re-read,
    /// re-propose through the fixer, retry once → still failing: the run FAILS — CommitOnHead
    /// never silently falls back from the requested commit/push to suggestion publication.
    /// (Proposals for files outside the changed-file manifest still degrade to suggestion:
    /// there is nothing committable to apply.) One editor per run; the writable set is
    /// proposal files ∩ changed-file manifest.</summary>
    private void ApplyProposalsToTree(
        ReviewContext ctx,
        RepoPathGuard guard,
        IReadOnlyDictionary<string, IFindingFixer> eligible,
        Dictionary<string, List<(RichFinding Finding, FixProposal Proposal, string[] Lines)>> proposalsByFile,
        List<AppliedFix> applied)
    {
        var changedFiles = ctx.Fetch.ChangedFiles
            .Select(RepoPath.Normalize)
            .ToHashSet(RepoPath.PathComparer);
        var writable = proposalsByFile.Keys
            .Where(changedFiles.Contains)
            .ToHashSet(RepoPath.PathComparer);
        var editor = _EditorFactory(guard, writable);

        foreach (var (path, proposals) in proposalsByFile.OrderBy(p => p.Key, RepoPath.PathComparer))
        {
            foreach (var (finding, proposal, lines) in proposals)
            {
                if (!writable.Contains(path) || !eligible.TryGetValue(finding.RuleId, out var fixer))
                {
                    RecordApplyFailure(finding, path, "file outside the writable set");
                    AttachFix(finding, applied, new AppliedFix(finding.DedupeKey!, proposal), ctx);
                    continue;
                }

                var appliedProposal = proposal;
                var expectedRangeHash = RangeHash(lines, proposal.StartLine, proposal.EndLine);
                var result = editor.ApplyRange(
                    path, proposal.StartLine, proposal.EndLine, proposal.Replacement, expectedRangeHash);

                if (!result.Success)
                {
                    // Drift retry, exactly once: re-read the current file, let the fixer
                    // re-propose against it, and re-apply with the fresh range hash.
                    string[]? freshLines = null;
                    try
                    {
                        freshLines = editor.ReadAllLines(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _Logger.LogWarning(ex, "auto-fix commit: could not re-read {Path} for rule {Rule}", path, finding.RuleId);
                    }

                    var retry = freshLines is null
                        ? null
                        : fixer.TryPropose(new FixContext(
                            finding, path, freshLines, ctx.Repository.Diff ?? DiffIndex.Parse(string.Empty)));
                    if (retry is not null && freshLines is not null)
                    {
                        var retryResult = editor.ApplyRange(
                            path, retry.StartLine, retry.EndLine, retry.Replacement,
                            RangeHash(freshLines, retry.StartLine, retry.EndLine));
                        if (retryResult.Success)
                        {
                            appliedProposal = retry;
                            result = retryResult;
                        }
                    }
                }

                if (!result.Success)
                {
                    // No silent fallback in CommitOnHead: the author asked for a committed
                    // fix; a fix that cannot be materialized fails the run visibly.
                    AutoFixTelemetry.AutoFixApplyFailed.Add(
                        1, new TagList {{"rule", finding.RuleId}});
                    throw new InvalidOperationException(
                        $"auto-fix commit: could not materialize the fix for rule {finding.RuleId} on {path} — {result.Error ?? "apply failed"}");
                }

                var fix = new AppliedFix(finding.DedupeKey!, appliedProposal) {AppliedToTree = result.Success};
                AttachFix(finding, applied, fix, ctx);
            }
        }
    }

    private void RecordApplyFailure(RichFinding finding, string path, string reason)
    {
        _Logger.LogWarning(
            "auto-fix commit: {Rule} on {Path} degraded to suggestion — {Reason}",
            finding.RuleId, path, reason);
        AutoFixTelemetry.AutoFixApplyFailed.Add(
            1, new TagList {{"rule", finding.RuleId}});
    }

    /// <summary>Drift-guard hash of the CURRENT content of [startLine..endLine] (1-based
    /// inclusive) — must match HashLineEditor.ApplyRange's computation exactly.</summary>
    private static string RangeHash(string[] lines, int startLine, int endLine)
        => HashLine.Of(string.Join('\n',
            lines.Skip(startLine - 1).Take(endLine - startLine + 1).Select(HashLine.Normalize)));

    private async Task RunCommandedPassAsync(
        ReviewContext ctx,
        string repoDir,
        RepoPathGuard guard,
        List<AppliedFix> applied,
        Func<int> getBudget,
        Action<int> setBudget,
        CancellationToken ct)
    {
        var commands = FixCommandDetector.Scan(
            ctx.Fetch.Threads, ctx.RequirePullRequest().CreatorId, ctx.Fetch.PriorRun?.LastObservedCommentAt);
        ctx.AutoFix = ctx.AutoFix with {FixCommands = commands};
        if (commands.Count == 0)
        {
            return;
        }

        var handledThreadIds = _Store is null
            ? new HashSet<long>()
            : (await _Store.GetCommandedFixThreadIdsAsync(ctx.Pr, ct).ConfigureAwait(false)).ToHashSet();
        var watermarkSkipped = 0;

        var replies = new List<(int ThreadId, string Text)>();
        var changedFiles = ctx.Fetch.ChangedFiles
            .Select(RepoPath.Normalize)
            .ToHashSet(RepoPath.PathComparer);
        // Audit rows are durable watermarks; a handled command must never spend fix budget twice.

        foreach (var command in commands.OrderBy(c => c.ThreadId))
        {
            ct.ThrowIfCancellationRequested();
            if (handledThreadIds.Contains(command.ThreadId))
            {
                watermarkSkipped++;
                replies.Add((command.ThreadId, "This fix command was already handled; not re-running."));
                continue;
            }

            var anchor = command.Anchor;
            var path = RepoPath.Normalize(anchor.FilePath);

            if (getBudget() <= 0)
            {
                replies.Add((command.ThreadId,
                    $"The fix budget for this run is exhausted ({_Options.MaxFixesPerRun} fixes). Reply /fixit again to re-queue for the next run."));
                continue;
            }

            // Dedupe: only active bot threads provide coverage; closed/fixed threads do not.
            var alreadyCovered =
                ctx.Fetch.Threads.Any(t =>
                    t.Status == ReviewThreadStatus.Active
                    && t.DedupeKey is not null
                    && t.Anchor is { } a
                    && string.Equals(RepoPath.Normalize(a.FilePath), path, RepoPath.PathComparison)
                    && RangesOverlap(a.StartLine, a.EndLine, anchor.StartLine, anchor.EndLine))
                || applied.Any(f =>
                    string.Equals(f.Proposal.FilePath, path, RepoPath.PathComparison)
                    && RangesOverlap(f.Proposal.StartLine, f.Proposal.EndLine, anchor.StartLine, anchor.EndLine));
            if (alreadyCovered)
            {
                replies.Add((command.ThreadId,
                    $"A fix covering {path}:{anchor.StartLine}–{anchor.EndLine} is already posted — see the suggestion on this thread's file."));
                continue;
            }

            var abs = guard.Resolve(path, out _);
            if (abs is null
                || !changedFiles.Contains(path)
                || (ctx.Repository.Diff?.NonReviewableFiles.ContainsKey(path) ?? false)
                || !File.Exists(abs))
            {
                replies.Add((command.ThreadId,
                    "this thread's file isn't part of the current diff — nothing to fix"));
                continue;
            }

            var snapshotBytes = await File.ReadAllBytesAsync(abs, ct);
            var editor = _EditorFactory(guard, new HashSet<string>(RepoPath.PathComparer) {path});
            // CommitOnHead: an accepted pass KEEPS its edits (they are the commit payload);
            // declined/failed passes still revert so a later "nothing to commit" stays accurate.
            // Suggestion mode reverts always, byte-identical to before.
            var keepEdits = false;
            try
            {
                var prompt = FixPromptBuilder.Build(command, path, anchor.StartLine, anchor.EndLine);
                var collector = new ReviewCollector();
                var pass = await _Agent.RunWithEditToolsAsync(
                        prompt,
                        collector,
                        ctx.Reasoning.ContextStore,
                        repoDir,
                        new HashSet<string>(RepoPath.PathComparer) {path},
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

                if (_Options.IsCommitOnHead && !collector.Done)
                {
                    // The pass edited the file but never completed task_done (iteration cap
                    // mid-work). Partial agent work is never published: revert the edit
                    // (keepEdits stays false) and treat the pass as failed.
                    _Logger.LogWarning(
                        "fix pass for thread {ThreadId}: discarding edits — task_done missing (iteration cap reached)",
                        command.ThreadId);
                    replies.Add((command.ThreadId,
                        "The fix pass did not complete, so no change was published. Reply /fixit to retry."));
                    continue;
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
                var acceptedFix = new AppliedFix($"{AppliedFix.CommandKeyPrefix}{command.ThreadId}", proposal)
                {
                    AppliedToTree = _Options.IsCommitOnHead,
                };
                keepEdits = _Options.IsCommitOnHead;
                AttachFix(null, applied, acceptedFix, ctx);
                setBudget(getBudget() - 1);
            }
            finally
            {
                if (!keepEdits)
                {
                    RevertFile(editor, guard, path, snapshotBytes);
                }
            }
        }

        ctx.AutoFix = ctx.AutoFix with {FixCommandReplies = replies};
        if (watermarkSkipped > 0)
        {
            AutoFixTelemetry.CommandedWatermarkSkipped.Add(watermarkSkipped);
        }
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
    }

    /// <summary>Reverts the file to its pre-pass state; a failed revert fails the run
    /// (a dirty pooled checkout would poison later runs).</summary>
    private static void RevertFile(
        HashLineEditor editor, RepoPathGuard guard, string path, byte[] snapshotBytes)
    {
        var resolved = guard.Resolve(path, out var resolveError)
                       ?? throw new IOException($"auto-fix revert path denied for {path}: {resolveError}");
        var directory = Path.GetDirectoryName(resolved)
                        ?? throw new IOException($"auto-fix revert path has no directory: {path}");
        if (new DirectoryInfo(directory).LinkTarget is not null)
        {
            throw new IOException($"auto-fix revert parent is a symlink: {directory}");
        }

        var temp = Path.Combine(directory, "." + Path.GetFileName(resolved) + ".rf-revert.tmp");
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            File.WriteAllBytes(temp, snapshotBytes);
            File.Move(temp, resolved, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        _ = editor.ReadAllLines(path);
        if (!File.ReadAllBytes(resolved).SequenceEqual(snapshotBytes))
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
        => new() {{"origin", origin.ToString().ToLowerInvariant()}, {"rule", rule}};
}