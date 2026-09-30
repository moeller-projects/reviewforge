# Implementation Plan A — Commit Infrastructure + `CommitOnHead` Publish Mode

First of two plans. This one builds the write path — commit, push, private checkout, loop
guard, conventional-commit construction — and activates it for the *existing* auto-fix
feature (stage 7.2). Plan B (resolve pipeline) builds on everything here and contains no
git machinery of its own.

## 0. Framing

1. **This is the smallest write-capable increment.** Stage 7.2 already produces reviewed,
   validated, budget-capped fixes from allowlisted authors. Switching their publication
   from "suggestion block" to "pushed commit" exercises the entire write path on the most
   trusted input ReviewForge has — before any free-text human comment is ever auto-applied.
2. **Push is the irreversible step; treat it as the run's point of no return.** Everything
   before push is disposable (private checkout). Everything after push must be truthful
   (replies cite only pushed SHAs) or fail loudly (rule 2: no silent fallback).
3. **AGENTS.md rules 6 and 8 are amended by design** (writes are no longer always reverted;
   publication is no longer suggestion-only). Amendment text in §11.

---

## 1. Scope

In scope:

- `IGitOps` write extensions: commit, fast-forward-only push, remote-tip read, tip-commit
  metadata read. LibGit2Sharp implementations + fakes.
- `PullRequest.SourceRefName` (domain + ADO adapter + fakes).
- Private run-scoped checkout (writes never touch the pooled per-head checkout).
- `ConventionalCommitBuilder` (pure) with `ReviewForge-Run:` trailer.
- Loop guard: bot-authored heads are not auto-re-enqueued; manual triggers still work.
- New pipeline stage 7.7 `commit-fixes` (order 77) + `CommitOnHead` activation in
  `AutoFixOptions` and the publish stage.
- Configuration, validation, telemetry, tests.

Explicitly out of scope (Plan B):

- Comment triage, free-text comment resolution, `/rf resolve`, `POST /resolutions`,
  run-kind on the queue, `resolve_actions` table, reply-per-outcome machinery.
- `StackedBranch` mode (remains reserved).
- Force-push, rebase, auto-merge, auto-approve. Never.

---

## 2. Safety model

Gate inventory for a CommitOnHead run (gates 1–4 exist today unchanged; 5–8 are new):

| # | Gate | Decides | Where |
|---|---|---|---|
| 1 | `AutoFix:Enabled` | feature present at all | composition root |
| 2 | `AllowedAuthors` (immutable creator ids) | this PR may receive fixes | `AutoFixFindingsStage` |
| 3 | `AllowedRuleIds` ∩ fixer registry / `/rf fix` command | this fix is eligible | stage 7.2 passes |
| 4 | Sandbox + writable set | write stays in-checkout, in-scope | `RepoPathGuard` + `HashLineEditor` |
| 5 | **Private checkout** | writes can't poison pooled checkouts or race a concurrent review | `PrepareRepositoryStage` |
| 6 | **Publish-mode validation** | `CommitOnHead` refuses to start without `CommitAuthorName`/`CommitAuthorEmail` | options validation at startup |
| 7 | **Publish guard + head pin** | claim still held; remote branch tip == pinned head | `CommitFixesStage` |
| 8 | **Loop guard** | own commit doesn't re-trigger automation | `ReviewGateStage` + discovery |

Failure semantics (rule 2 — visible, never silent):

| Failure | Outcome |
|---|---|
| Proposal can't be applied (hash drift, unreadable file) | That fix degrades to suggestion-mode publication; run continues. Degradation is logged + counted, never silent |
| Agent fix pass declines | Existing reply path, unchanged |
| Commit fails | Run fails |
| Remote tip moved before push | Run fails with `PrHeadChangedException` (existing type — reused, not a new exception) |
| Push rejected (non-FF, policy, auth) | Run fails; committed work dies with the private checkout; no retry with force |
| Crash between push and publish | Run row is an in-flight shell (begin-run already ran at order 75); `ShellReaperService` finalizes it as failure; re-run is safe because publish's reply dedupe matches on posted text |

---

## 3. Component 1 — Port and domain extensions

### 3.1 `PullRequest.SourceRefName`

```csharp
public sealed record PullRequest(
    int Id,
    string Title,
    string? Description,
    string SourceCommitSha,
    string TargetCommitSha,
    string CloneUrl,
    bool IsDraft,
    string CreatorId,
    string CreatorName,
    string? SourceRefName);        // new, last, optional — "refs/heads/feature/x"
```

- ADO adapter (`AdoPullRequestSource.GetPullRequestAsync`): map `gpr.SourceRefName`.
  One line; the class is `[ExcludeFromCodeCoverage]` (vendor wrapper), so no coverage hit.
- `ReviewForge.Testing.FakePullRequestSource`: default `refs/heads/feature/test`; tests
  that assert push refspecs set it explicitly.
- Null/empty means the provider didn't say → CommitOnHead treats the run as ineligible
  (log + degrade all fixes to suggestion mode). Suggestion mode never reads the field.

### 3.2 `IGitOps` write surface

```csharp
public interface IGitOps
{
    // … existing members unchanged …

    /// <summary>Commits all staged worktree changes; returns the new commit SHA.
    /// The author/committer identity is supplied by the caller (never derived from config
    /// inside the adapter).</summary>
    Task<string> CommitAsync(
        string repoPath, string message, string authorName, string authorEmail, CancellationToken ct);

    /// <summary>Fast-forward-only push of HEAD to refs/heads/{remoteBranch}. Throws when the
    /// remote tip is not <paramref name="expectedRemoteTipSha"/> (head pin) or the push is
    /// rejected. Never forces.</summary>
    Task PushAsync(
        string repoPath, string remoteBranch, string expectedRemoteTipSha, string? pat, CancellationToken ct);

    /// <summary>Current tip SHA of refs/heads/{remoteBranch} on the remote (ls-remote),
    /// or null when the branch doesn't exist.</summary>
    Task<string?> GetRemoteTipAsync(
        string repoPath, string remoteBranch, string? pat, CancellationToken ct);

    /// <summary>Author email + full message of the given commit, for the loop guard.
    /// Null when the commit isn't present locally.</summary>
    Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct);
}

[ExcludeFromCodeCoverage]
public sealed record TipCommitInfo(string AuthorEmail, string Message);
```

### 3.3 LibGit2Sharp implementation (`LibGit2SharpGitOps`)

- All four members run on the existing `GitOperationScheduler` (bounded dedicated threads);
  cancellation stops waiting promptly, as with the read members.
- `CommitAsync`: `Commands.Stage(repo, "*")` + `repo.Commit(message, author, committer)`
  with caller-supplied `Signature` built from name/email + `TimeProvider`-free
  `DateTimeOffset.Now` (adapter is excluded from coverage; determinism lives in the
  message builder, not the timestamp). Empty worktree → throws
  `InvalidOperationException("nothing to commit")` — the stage treats this as "no fixes
  survived application", not as a crash (§5.4).
- `PushAsync`:
  - Refspec `+`-free: `refs/heads/{branch}:refs/heads/{branch}` — no force flag anywhere.
  - Pin check first: `GetRemoteTipAsync` must equal `expectedRemoteTipSha`; mismatch →
    throw `PrHeadChangedException` (reuse the existing pipeline exception; it already
    carries the right semantics and the worker already knows how to log it).
  - Credentials: same PAT the clone path uses, restricted by the existing
    `_CredentialHost` check (the PAT is never presented to a host other than
    `Ado:OrgUrl`'s host — this constraint now also covers push).
  - Rejection (branch policy, non-FF, auth) → throw with the server's message verbatim.
- `GetRemoteTipAsync`: `git ls-remote` equivalent — LibGit2Sharp
  `Repository.ListRemoteReferences` or a fetch-then-read of the tracking ref; prefer
  `ListRemoteReferences` (no ref mutation).
- `GetCommitInfoAsync`: `repo.Lookup<Commit>(sha)` → author email + message.

### 3.4 Fakes

`FakeGitOps` gains in-memory implementations: a commit appends to a fake history list;
push validates the pinned tip against a mutable `RemoteTip` property (tests flip it to
trigger the pin failure); `GetCommitInfoAsync` reads the fake history. This keeps every
Core test able to exercise commit/push paths without LibGit2Sharp.

---

## 4. Component 2 — Private run-scoped checkout

### 4.1 Why

The pool keys checkouts `(repositoryId, headSha)` and hands out leases; a second review
run on the same head reuses the same tree. Any persistent write would be visible to that
run (and would survive past the writing run's lease). 7.2's revert discipline exists
precisely because of this sharing. Committed mode makes revert semantics unworkable —
the writes must survive until push, then the tree is garbage.

### 4.2 Design

```csharp
public sealed class RepoCheckoutPool
{
    // … existing …

    /// <summary>Run-scoped writable checkout: mirror-local clone into
    /// {root}/private/{runId}, never shared, deleted on lease disposal.
    /// Uses the same per-mirror lock as AcquireAsync for the fetch side.</summary>
    public async Task<RepoCheckout> AcquirePrivateAsync(
        Guid runId, string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct);
}
```

- Implementation: reuse the mirror for object storage (`git clone --shared` semantics via
  LibGit2Sharp clone with the mirror path as source + the clone URL configured as remote),
  checkout `headSha` detached, same `EnsureCommitsAsync` path for targeted fetch.
- The returned `RepoCheckout` lease deletes `{root}/private/{runId}` on `Dispose` via
  `IWorkspaceFs` (recursive delete; failure to delete is logged, never thrown — eviction
  sweeps reap strays; add `{root}/private` to `CheckoutEvictionWorker`'s scope).
- `ReviewContext.Dispose` already disposes `RepoLease` — no context change.

### 4.3 Stage wiring

`PrepareRepositoryStage` gains a mode selector (constructor arg, set by the composition
root):

```csharp
public enum CheckoutMode { Pooled, Private }
```

Composition root passes `Private` when `AutoFix:PublishMode == "CommitOnHead"` (and always
for Plan B's resolve pipeline). Everything else in the stage — threads-refresh overlap,
diff build, reviewable-file computation — is unchanged.

**Interaction with 7.2's revert discipline:** in `CommitOnHead` mode the commanded pass
does *not* call `RevertFile` for accepted fixes (their edits are the payload). Reverts
still happen for declined/failed passes so `nothing to commit` stays accurate. In
`Suggestion` mode the revert behavior is byte-identical to today.

---

## 5. Component 3 — `ConventionalCommitBuilder` (pure)

New file `src/ReviewForge.Core/AutoFix/ConventionalCommitBuilder.cs`. Pure static: no IO,
no clock, fully unit-tested.

```csharp
public static class ConventionalCommitBuilder
{
    public const string RunTrailerName = "ReviewForge-Run";
    public const int MaxSubjectChars = 72;
    public const int BodyWrapCol = 72;

    /// <summary>One commit message for a set of fixes sharing a commit.</summary>
    public static string Build(CommitInput input);
}

public sealed record CommitInput(
    IReadOnlyList<AppliedFix> Fixes,       // 1..n, all landing in this commit
    IReadOnlyList<RichFinding> Findings,   // finding per deterministic fix (null-entry for commanded)
    int PrId,
    Guid RunId);
```

Rules:

1. **Type** from the finding category of the first fix (deterministic) or `fix` for
   commanded fixes: bug→`fix`, security→`fix`, performance→`perf`, style→`style`,
   docs→`docs`. Mixed sets → type of the highest-severity finding's category.
2. **Scope**: top-level directory of the first fix's `FilePath`, lowercased, sanitized to
   `[a-z0-9-]`; omitted when empty or unsanitizable. `src/api/Foo.cs` → `(api)`.
3. **Subject**: `{type}({scope}): {summary}` — summary is the fix's rationale
   (deterministic: `FixProposal.Rationale`; commanded: the one-sentence TaskDone summary),
   first line only, trailing period stripped, hard-truncated at 72 chars total.
4. **Body**: blank line, then the full rationale wrapped at 72; for multi-fix commits, one
   bullet per fix (`- path:start-end — rationale`).
5. **Trailers** (blank line, then):
   - `Refs: !{prId}` always; `Refs: !{prId} thread {threadId}` for commanded fixes.
   - `ReviewForge-Run: {runId}` always — this is the loop-guard marker (§6).
6. No `Co-authored-by` in this plan (identity question deferred — see Plan B open
   questions; bot commits stand alone).

---

## 6. Component 4 — Loop guard

Pushing to the PR source branch creates a new head → discovery sees "updated PR" → without
a guard, ReviewForge reviews its own commit forever.

1. **Trigger provenance.** `ReviewRequest` gains
   `EnqueueTrigger Trigger = EnqueueTrigger.Manual` (`Manual` | `Discovery`), propagated to
   `ReviewContext.Trigger`. `POST /reviews` → `Manual`; `DiscoveryService` → `Discovery`.
   (Queue schema: additive nullable column, `EnsureSchema`'s guarded-`ALTER TABLE` pattern
   already used for `LastObservedCommentAt`; null reads as `Manual` — safe because only
   pre-upgrade rows lack it and those are all from the manual endpoint era... no: durable
   rows may be discovery-enqueued today. Treat null as `Discovery`? No — null rows predate
   the guard, their head is not a bot head anyway, so either default is harmless. Default
   `Manual`.)
2. **Bot-head detection.** In `FetchPrContextStage`, when
   `AutoFix:PublishMode == "CommitOnHead"` (or Plan B resolve enabled), call
   `GetCommitInfoAsync(repoPath…)` — *after* stage 3 materializes the checkout, so do it in
   `PrepareRepositoryStage` instead: read tip info for `SourceCommitSha`, store
   `ctx.HeadCommitInfo`. Cheap, local, one lookup.
3. **Gate rule.** `ReviewGateStage`: when `ctx.Trigger == Discovery` and
   `HeadCommitInfo.Message` contains `ReviewForge-Run:` → `Terminate("bot-authored head")`.
   Manual `POST /reviews` on a bot head still reviews (a human asked; reviewing the bot's
   commit is desirable).
4. **Discovery-side filter** (defense in depth): `DiscoveryService` skips enqueue when the
   candidate's head message carries the trailer. ADO's PR listing doesn't expose commit
   messages cheaply, so this filter is best-effort at the sweep (uses the warmup path's
   mirror when present); the gate in (3) is the correctness path.
5. Telemetry: `reviewforge.loopguard.skips` counter, tag `source=gate|discovery`.

`ReviewGate` (pure) stays untouched — the rule lives in the stage because it needs IO
results (`HeadCommitInfo`) and trigger provenance, neither of which the pure function has.
Document that boundary in the stage's remarks.

---

## 7. Component 5 — `CommitOnHead` activation

### 7.1 Options and validation

`AutoFixOptions`:

```csharp
/// <summary>"Suggestion" (default) | "CommitOnHead". "StackedBranch" remains reserved.</summary>
public string PublishMode { get; init; } = "Suggestion";

/// <summary>Commit granularity in CommitOnHead mode: "PerFix" (default; one commit per
/// applied fix — cleanest thread↔SHA correlation) | "Single" (one commit per run).</summary>
public string CommitGranularity { get; init; } = "PerFix";

/// <summary>Commit identity. Required when PublishMode=CommitOnHead.</summary>
public string? CommitAuthorName { get; init; }     // e.g. "reviewforge[bot]"
public string? CommitAuthorEmail { get; init; }    // e.g. "reviewforge@contoso.com"
```

`ServiceCollectionExtensions` validation replaces the single-mode check:

- `PublishMode` ∈ {`Suggestion`, `CommitOnHead`} (case-sensitive, ordinal — config is
  machine-authored) else startup error naming the valid values.
- `CommitOnHead` ⇒ `CommitAuthorName` and `CommitAuthorEmail` non-empty, else startup error.
- `CommitGranularity` ∈ {`PerFix`, `Single`}.
- Update `OptionsValidationTests`: the current test asserting only `"Suggestion"` passes
  becomes a theory over both modes + the identity requirement.

### 7.2 Stage 7.2 changes (`AutoFixFindingsStage`)

The stage's *decisions* are unchanged (gates, budgets, fixer registry, command detection).
What changes is the *materialization*:

**Deterministic pass (CommitOnHead):** proposals are applied to the private checkout
instead of only attached:

```csharp
var expectedRangeHash = HashLine.Of(string.Join('\n',
    lines.Skip(proposal.StartLine - 1).Take(proposal.EndLine - proposal.StartLine + 1)
         .Select(HashLine.Normalize)));
var result = editor.ApplyRange(
    proposal.FilePath, proposal.StartLine, proposal.EndLine,
    proposal.Replacement, expectedRangeHash);
```

- `editor` is one `HashLineEditor` for the run with writable set = proposal files ∩
  changed-file manifest (same construction as the commanded pass, multi-file).
- `ApplyRange` hash mismatch (the file drifted since the fixer read it — e.g. two proposals
  on overlapping ranges): re-read the file once, re-propose through the fixer, retry once;
  still failing → degrade that fix to suggestion publication, count
  `reviewforge.autofix.apply_failed_total`. Never silent.
- Applied proposals keep flowing into `ctx.AppliedFixes`; a new nullable
  `AppliedFix.CommitSha` (set later by stage 7.7) records the outcome.

**Commanded pass (CommitOnHead):** identical agent pass; the `finally { RevertFile(...) }`
becomes conditional — accepted fixes keep their edits; declined/failed passes still revert.

**`AppliedFix` record:** gains `string? CommitSha { get; set; }` (settable — stage 7.7
runs after 7.2). Serialization is additive; old stored JSON deserializes with null.

### 7.3 New stage 7.7 — `CommitFixesStage` (order 77)

Placement is deliberate: **after** `BeginRunStage` (75). Push is an external write; the
in-flight shell must exist before it so a crash between push and publish is reaped and
visible, and so the `ReviewForge-Run:` trailer references a row that exists.

```csharp
public sealed class CommitFixesStage(
    IGitOps git,
    AutoFixOptions options,
    ILogger<CommitFixesStage> logger) : IReviewStage
{
    public string Name => "commit-fixes";
    public int Order => 77;
}
```

Flow:

1. No-op unless `PublishMode == "CommitOnHead"` and `ctx.AppliedFixes` is non-empty.
2. `ctx.PublishGuard` check (claim still held) — same guard publish uses.
3. Group fixes per `CommitGranularity` (`PerFix`: each fix its own commit, in applied
   order; `Single`: one commit).
4. Per group: stage that group's files only — `CommitAsync` stages the whole worktree, so
   `PerFix` grouping requires either per-group staging (extend `CommitAsync` with an
   optional `paths` parameter — **take this option**: `IReadOnlyList<string>? paths`,
   null = all) or applying groups lazily. Chosen: `paths` parameter, fixes within a group
   were applied sequentially and non-overlapping per file-range checks.
   - Wait — fixes were *all applied* in stage 7.2. With `PerFix`, staging by path only
     separates at file granularity; two fixes on the *same file* land in one commit
     regardless. Accept and document: granularity is per-fix *where file boundaries allow*.
5. Message via `ConventionalCommitBuilder`; `CommitAsync`; record SHA onto each
   `AppliedFix.CommitSha`.
6. Nothing committed (all fixes degraded) → stage exits cleanly; publish degrades to
   suggestion mode for those fixes (§7.4).
7. Head pin: `GetRemoteTipAsync(repoDir, branch)` must equal `pr.SourceCommitSha`;
   else throw `PrHeadChangedException` → run fails, discovery re-enqueues on the new head.
8. `PushAsync(repoDir, branch, pr.SourceCommitSha, pat)`. One push for all commits.
   `pr.SourceRefName` must be non-empty and start with `refs/heads/`; otherwise degrade the
   whole run's fixes to suggestion mode *before any commit* (check at step 2).

### 7.4 Publish changes (`PublishFindingsStage`)

- `CommentFormatter` gains `FormatCommittedFix(finding, fix)`:
  `Fixed in {sha7} — {commitSubject}\n\n{rationale}` (plus the standard bot preamble via
  `WithBotPreamble`).
- Findings whose `AppliedFix.CommitSha` is set publish the committed-fix body instead of
  the suggestion block — as a **reply on the existing thread** when one is live (the
  `liveFixedReplies` path already exists for exactly this), or as the finding thread body
  for first-time findings. Dedupe keys, suppression, regression handling: unchanged.
- Fixes that degraded (no SHA) publish exactly as today's suggestion mode. A run can
  legitimately contain both.
- Commanded-fix replies (`FixCommandReplies`) gain the SHA line: stage 7.7 rewrites the
  queued "suggestion posted" reply text for thread-fixes that committed… — no: simpler
  and less clever — 7.2 queues *no* success replies in CommitOnHead mode; stage 7.7
  appends `Fixed in {sha7}` replies to `ctx.FixCommandReplies` for committed thread-fixes.
  Decline/budget replies are still queued by 7.2 unchanged.
- `PostSuggestionThreadAsync` is not used in CommitOnHead mode; the thread already exists
  (the command's own thread), so `ReplyToThreadAsync` suffices.

### 7.5 Persistence

- `AppliedFixPersistence` serializes the new `CommitSha` field automatically (JSON).
  No schema change.
- The finding rows' semantics are unchanged; a committed fix is still a finding with an
  applied fix attached — triage's "no longer reproduces" auto-resolve will close its
  thread on the next run after the push (the head moved, the finding is gone from the new
  diff). That is the desired self-cleaning behavior; note it in the stage remarks.

---

## 8. Telemetry

New counters on `ReviewForgeTelemetry` (same naming/tag conventions):

- `reviewforge.autofix.commits_total` (tags: granularity)
- `reviewforge.autofix.pushes_total` / `reviewforge.autofix.push_failures_total` (tags: reason=pin|rejected)
- `reviewforge.autofix.apply_failed_total` (tags: rule)
- `reviewforge.autofix.degraded_to_suggestion_total`
- `reviewforge.loopguard.skips_total` (tags: source)
- `reviewforge.checkout.private_total` + duration histogram reuse via existing checkout metrics with a `kind=private` tag

## 9. Configuration

```jsonc
"AutoFix": {
  "Enabled": true,
  "AllowedAuthors": ["<immutable-creator-id>"],
  "AllowedRuleIds": ["bash-set-e-missing", "..."],
  "PublishMode": "CommitOnHead",          // was: only "Suggestion" valid
  "CommitGranularity": "PerFix",          // | "Single"
  "CommitAuthorName": "reviewforge[bot]",
  "CommitAuthorEmail": "reviewforge@contoso.com",
  "MaxFixesPerRun": 3,
  "EnableThreadFixCommands": true,
  "FixPassMaxIterations": 8
}
```

No new secrets. Push reuses `REVIEWFORGE_ADO_PAT`; the PAT needs *Contribute* on the repo
(it already needs *Read*; document the permission bump in README). Branch policies must
permit the bot identity to push to PR source branches — document that a policy rejecting
the push fails the run visibly (this is the intended behavior).

## 10. Tests (≥95% line coverage on touched assemblies, rule 5)

**Core:**
- `ConventionalCommitBuilderTests` — type mapping (each category), mixed-set severity rule,
  scope sanitization (empty, nested, unicode, dots), 72-char truncation, body wrapping,
  trailers (`ReviewForge-Run`, `Refs` with/without thread), PerFix vs Single inputs.
- `AutoFixStageCommitTests` — deterministic apply via `ApplyRange`; hash-drift → re-propose
  once → degrade; commanded pass skips revert when accepted, reverts when declined;
  writable-set construction; `CommitSha` attached.
- `CommitFixesStageTests` — no-op conditions; publish-guard abort before any commit;
  `SourceRefName` missing → degrade-before-commit; pin mismatch → `PrHeadChangedException`;
  push rejection → run fails; PerFix same-file coalescing documented by test; reply text
  appended for thread-fixes.
- `PipelineTests` — end-to-end CommitOnHead run on fakes: fix → commit → push → thread
  body contains sha7; mixed run (one committed, one degraded) publishes both forms.
- `ReviewGateStageTests` / gate-adjacent — discovery-triggered run on bot-trailer head
  terminates; manual trigger proceeds; `HeadCommitInfo` null (commit absent) → proceed.
- `PrepareRepositoryStageTests` — private mode returns a lease whose dispose deletes the
  dir (IWorkspaceFs fake); pooled mode byte-identical.

**Infrastructure:**
- `LibGit2SharpGitOps` is `[ExcludeFromCodeCoverage]` (vendor wrapper) — smoke-test via the
  existing infrastructure test pattern where feasible (local bare remote: commit/push/pin/
  ls-remote happy path + non-FF rejection). No coverage obligation, but the pin logic is
  worth the integration test.
- `SqliteReviewQueueTests` — trigger column round-trip; pre-upgrade row (null) reads as
  `Manual`.

**Service:**
- `OptionsValidationTests` — both modes; identity required for CommitOnHead; granularity
  values; error messages.
- `EndpointsTests` — trigger propagation `Manual` on POST.
- `DiscoveryFilterTests` — trailer-carrying head skipped when mirror info available.

## 11. AGENTS.md amendments

- **Rule 6** gains: *"`HashLineEditor` writes are reverted in Suggestion mode. In
  CommitOnHead mode (`AutoFix:PublishMode`), accepted fixes are NOT reverted — they are
  committed by stage 7.7. CommitOnHead runs use a private run-scoped checkout
  (`RepoCheckoutPool.AcquirePrivateAsync`); the pooled per-head checkout never sees writes.
  The writable-set, containment, deny-regex and hash-anchor disciplines are unchanged."*
- **Rule 8** gains: *"…(c) in CommitOnHead mode additionally the run holds its PR claim at
  push time and the remote branch tip equals the run's pinned head (stage 7.7); push is
  fast-forward-only, never forced."*
- README stage table: insert 7.7 `commit-fixes`; update the PublishMode comment and the
  PAT permission note (Contribute).

## 12. File-by-file change list

```
src/ReviewForge.Core/
  Domain/Models.cs                      PullRequest + SourceRefName
  Ports/IGitOps.cs                      CommitAsync / PushAsync / GetRemoteTipAsync /
                                        GetCommitInfoAsync / TipCommitInfo
  Ports/IReviewQueue.cs                 ReviewRequest + Trigger; EnqueueTrigger enum
  Pipeline/ReviewContext.cs             + Trigger, + HeadCommitInfo
  Pipeline/Stages/PrepareRepositoryStage.cs   CheckoutMode selector; HeadCommitInfo fill
  Pipeline/Stages/ReviewGateStage.cs    loop-guard rule
  Pipeline/Stages/AutoFixFindingsStage.cs     CommitOnHead materialization, conditional revert
  Pipeline/Stages/CommitFixesStage.cs   NEW (order 77)
  Pipeline/Stages/PublishFindingsStage.cs     committed-fix bodies/replies
  Pipeline/CommentFormatter.cs          FormatCommittedFix
  AutoFix/AutoFixOptions.cs             PublishMode / CommitGranularity / identity
  AutoFix/ConventionalCommitBuilder.cs  NEW (pure)
  AutoFix/IFindingFixer.cs              AppliedFix + CommitSha (nullable settable)
  Workspaces/RepoCheckoutPool.cs        AcquirePrivateAsync
src/ReviewForge.Infrastructure/
  Ado/AdoPullRequestSource.cs           map SourceRefName
  Git/LibGit2SharpGitOps.cs             4 new members (scheduler, credential-host check)
  Persistence/SqliteReviewQueue.cs      trigger column (guarded ALTER)
src/ReviewForge.Service/
  ServiceCollectionExtensions.cs        validation
  ReviewPipelineFactory.cs              wire CheckoutMode + CommitFixesStage
  Endpoints.cs                          Trigger=Manual
  DiscoveryService.cs                   Trigger=Discovery + best-effort bot-head filter
  CheckoutEvictionWorker.cs             sweep {root}/private
  appsettings.json / .env.example       documented defaults
tests/ReviewForge.Testing/
  FakeGitOps / FakePullRequestSource    write-surface fakes
tests/…                                 per §10
```

## 13. Rollout

1. Ship with `PublishMode: "Suggestion"` everywhere — infrastructure inert, all new code
   behind the mode check, byte-identical pipeline otherwise.
2. Enable `CommitOnHead` on **one** allowlisted author in one repo, deterministic fixers
   only (`EnableThreadFixCommands: false`). Watch `push_failures_total`,
   `apply_failed_total`, loopguard skips.
3. Enable commanded fixes in CommitOnHead for the same pilot.
4. Widen author/rule allowlists per team request. `StackedBranch` stays reserved until a
   repo policy demands it.

## 14. Open questions

1. **Same-file PerFix coalescing** — acceptable (documented) or must commits be strictly
   per-fix (requires lazy application per group; rejected: re-introduces apply ordering
   complexity for zero audit gain — the trailer + body bullets carry per-fix attribution)?
2. **Branch-policy gate** — should a policy rejection *degrade to suggestion* instead of
   failing the run? Recommended: fail visibly (rule 2); a repo that forbids bot pushes is
   a configuration fact, not a transient error. Revisit after pilot telemetry.
3. **Commit signing** — ADO doesn't require it; if a repo enables "require signed commits",
   the push fails per (2). Out of scope until demanded.
4. **Suggestion fallback for degraded fixes** — keep (chosen) or reply-only? Chosen: keep,
   because a degraded fix is still a validated fix and the suggestion is its safe vehicle.
