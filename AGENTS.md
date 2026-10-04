# AGENTS.md — ReviewForge

Guidance for coding agents working in this repository. Read this before editing.

## What this is

Automated PR review and autonomous comment resolution as a service (.NET 10, ASP.NET
minimal API). Review requests run the 12-stage review pipeline; opt-in resolution requests
run a separate pipeline that fetches context → gates → prepares a private checkout →
collects comments → read-only triage → bounded edits → verification → commit/push →
replies → persist. See `README.md` for the stage table.

## Layout

```
src/
  ReviewForge.Core/            pure domain + pipeline + agent loop. NO IO adapters, NO vendor SDKs.
    Domain/                    models, ReviewGate, RunClassifier, ThreadTriage, FailureBackoff
    Analysis/                  DedupeKey (shift-proof), DiffIndex, AnchorResolver, ShardPlanner,
                               homoglyph analyzer, LineEditEngine, PathSafety
    Reasoning/                 NativeReviewAgent, RepoReadTools, RepoPathGuard, prompt building,
                               RuleBook + embedded rule-pack JSONs, embedded system prompts
    AutoFix/                   FindingFixerRegistry + deterministic fixers, FixPromptBuilder,
                               AppliedFixPersistence
    Workspaces/                RepoCheckoutPool (per-head checkout leases, idle eviction)
    Pipeline/                  ReviewPipeline, Stages/ (one class per stage), CommentFormatter
    Ports/                     IPullRequestSource, IFindingStore, IGitOps, IChatClientFactory,
                               IContextEnricher, IReviewQueue, IWorkspaceFs
  ReviewForge.Infrastructure/  adapters: Ado/ (ADO SDK + retry), Git/ (LibGit2Sharp + scheduler),
                               Codex/ (OAuth file), Chat/ (OpenAI/Codex clients + LLM governor),
                               Persistence/ (SQLite via EF Core), Filesystem/ (IWorkspaceFs)
  ReviewForge.Service/         host: Endpoints, Queue/ (ReviewQueue + RunTracker, InFlightClaims,
                               ClaimHeartbeat), Security/ (API-key filter),
                               hosted workers (ReviewWorker, DiscoverySweepWorker,
                               CheckoutEvictionWorker, ShellReaperService),
                               ReviewPipelineFactory (composition root), Program.cs
  ReviewForge.AppHost/         Aspire orchestration (dev dashboard, OTLP injection, secret params)
  ReviewForge.Cli/             thin HTTP client (submit / status) — all logic is server-side
tests/
  ReviewForge.Testing/         shared fakes (FakePullRequestSource, FakeFindingStore, FakeGitOps,
                               FakeEnricher, FakeChatClientFactory) + ScriptedChatClient
  ReviewForge.{Core,Infrastructure,Service}.Tests/
  ReviewForge.Architecture.Tests/  assembly-boundary tests; no coverlet gate by design
prompts/fix-pass-system.md     human-editable copy of the embedded fix-pass prompt; the review
                               prompt override is ReviewForge:PromptOverridePath, and the embedded
                               default lives in src/ReviewForge.Core/Reasoning/Prompts/
```

## Hard rules (do not violate)

1. **Ports and adapters.** `ReviewForge.Core` must stay free of IO adapters and vendor
   SDKs. New PR host → implement `IPullRequestSource`. New reasoning provider → implement
   `IChatClientFactory`. Enrichment → `IContextEnricher` (fail-safe contract). The
   pipeline must not change for a new adapter. Direct-IO exception, deliberate:
   `Analysis/PathSafety.cs` performs symlink resolution (Directory/File.Exists, LinkTarget)
   as the agent sandbox's containment primitive. It is isolated behind PathSafety so it
   can be swapped for a port if Core is ever hosted out-of-process.
   `Reasoning/HashLineEditor.cs` (author-commanded fix passes)
   is under the same direct-IO exception; see rule 6 for the writable-set discipline. All
   other Core IO goes through `IWorkspaceFs` or is stage-local run-artifact IO documented
   in `<remarks>` (see the `IWorkspaceFs` scope note).
2. **No engine fallback.** A failed stage fails the run; the failure is visible in run
   status. Never swallow an exception, never add a silent fallback engine or provider.
3. **Secrets from the environment only.** ADO PAT: `REVIEWFORGE_ADO_PAT`. OpenAI key:
   `OPENAI_API_KEY`. Codex OAuth: `~/.codex/auth.json`. Never commit, log, or serialize
   secret values.
4. **Persistence discipline.** Skipped runs (gate-terminated) are never persisted — a
   draft skip must not mark a head as reviewed.
5. **Coverage gate.** Every production-code test project enforces ≥95% line coverage
   (coverlet `Threshold=95`) on its own SUT assembly across all operating systems.
   The `ReviewForge.Architecture.Tests` project is the explicit exception: it validates
   assembly boundaries and covers no production code, so it deliberately has no coverlet
   threshold. Any logic added to a production-code test project must be covered or explicitly
   excluded with justification
   (`[ExcludeFromCodeCoverage]` is reserved for pure vendor-SDK wrappers like
   `AdoPullRequestSource`, `LibGit2SharpGitOps`, `Program.cs`).
6. **Agent sandbox.** `RepoReadTools` (agent reads) is read-only, rooted at the checkout,
   escape-proof, deny-regex for `.git`, `.env*`, `*.pem`, `*.key`, `secrets`. The agent has
   no shell. `HashLineEditor` (used by author-commanded fix passes) shares the same
   containment and deny rules via `RepoPathGuard` and is
   additionally limited to a per-run writable set (a single anchored file for fix passes).
   `HashLineEditor` writes are reverted in Suggestion mode. In CommitOnHead mode
   (`AutoFix:PublishMode`), accepted fixes are NOT reverted — they are committed by stage
   7.7. CommitOnHead runs use a private run-scoped checkout
   (`RepoCheckoutPool.AcquirePrivateAsync`); the pooled per-head checkout never sees writes.
   The writable-set, containment, deny-regex and hash-anchor disciplines are unchanged.
   Resolve runs use the same `HashLineEditor` discipline in a private run-scoped checkout,
   with each writable set computed from triaged, commenter-authorized, anchored comments.
   Their triage pass is structurally read-only: no edit or finding-recording tools are registered.
   The agent never invokes external processes.
7. **Codex auth file** is rewritten atomically (temp + move) on token rotation. Any mount
   or path you introduce must preserve that (directory mount, read-write).
8. **Auto-fix discipline.** A fix is only published when (a) the author is allowlisted and
   (b) the rule has a registered fixer or the fix was explicitly commanded by the PR
   author via `/rf fix`. Any gate failing means the finding
   is published as a plain comment instead. In Suggestion mode every fix is published as an
   ADO suggestion block. In CommitOnHead mode (`AutoFix:PublishMode`) fixes are committed in
   the run's private checkout and pushed fast-forward-only to the PR source branch, and then
   only when (c) the run holds its PR claim immediately before push, the remote branch tip
   equals the pinned head at the pre-read, and the push itself is a fast-forward-only
   compare-and-swap (stage 7.7). Pushed outcomes are persisted before any reply is attempted
   (pushed_fixes), and a later run reconciles missing replies. ReviewForge never force-pushes,
   never rebases, never opens pull requests. AI-drafted fixes are always labeled as such.
   Resolve fixes are committed only after triage and bounded writable-set planning, then only
   under the same claim/head-pin/fast-forward guards. Discovery and `/rf resolve` command
   runs require the immutable creator allowlist and comment watermark; an authenticated
   manual `POST /resolutions` bypasses those two checks but still rejects drafts and shares
   the PR claim. Resolve outcomes are persisted before replies so a retry can reconcile them.
   Bot replies never close human threads by default. Never resolve another person's thread.
   Never leave a pooled checkout dirty.

## Conventions

- `Directory.Build.props`: `net10.0`, nullable enable, implicit usings, `LangVersion=latest`.
  Experimental APIs suppressed centrally (`MAAI001`, `OPENAI001`, `MEAI001`).
- Options are typed records/classes with DataAnnotations, validated fail-fast at startup.
  Config sections: `Ado`, `Reasoning`, `ReviewForge`, `Api`, `ApiDocs`, `AutoFix`, `Resolve`,
  `Discovery`, `RepoReadTools`. Env overrides use double underscore,
  e.g. `ReviewForge__MaxIterations=50`.
- Tests: xUnit, no mocking framework — hand-written fakes live in `ReviewForge.Testing`.
  Service tests run the real host in-process via `WebApplicationFactory<Program>` with
  fakes swapped in DI. `ScriptedChatClient` scripts the agent loop's tool calls.
  New doubles go to `ReviewForge.Testing` unless provably single-use; dependency-direction
  changes require an architecture-test change in the same PR.
  `ReviewForge.Testing` is the canonical shared-double layer; configure its narrow
  `ThrowOn*` knobs for failure paths instead of adding duplicate throwing subclasses.
- Commits: Conventional Commits (`feat`, `fix`, `refactor`, `test`, …), imperative,
  no trailing period.

## Commands

```bash
dotnet build                                        # build everything
dotnet test                                         # full suite incl. coverage gates
dotnet test tests/ReviewForge.Core.Tests /p:CollectCoverage=true
dotnet run --project src/ReviewForge.Service        # serves http://localhost:5080
```

Service endpoints: `POST /reviews` and opt-in `POST /resolutions` → 202 `{runId, statusUrl}`
(400 validation, 401 bad key, 409 PR already in flight, 429 rate-limited, 503 queue full) ·
`GET /reviews/{runId}` · `POST /reviews/discover` · `GET /health` (store-backed) · `GET /alive` (liveness). The
ingest queue has a fixed capacity of 100; `ReviewForge:WorkerCount` sets the workers
(default processorCount/2 clamped 2–8, validated 1–64). `RunTracker` is a bounded
in-memory status cache (24 h retention, 10k entries, sticky terminal states); status
read-through falls back to the durable queue row and then the store row, so with
`QueueMode=Sqlite` runs stay visible across restarts.

All production-code test projects enforce at least 95% line coverage across all operating
systems; `ReviewForge.Architecture.Tests` is exempt. Do not document generated test counts.

## Docker

`Dockerfile` is multi-stage: restore layer (project files only) → publish → alpine
runtime (non-root `app` user, uid 1654; healthcheck on `/health`). LibGit2Sharp and
SQLite use their bundled native binaries; no `git` binary is needed.

```bash
docker compose up --build        # needs REVIEWFORGE_ADO_PAT (plus ADO_ORG_URL/ADO_PROJECT)
docker logs -f reviewforge
```

The compose service is hardened: read-only rootfs (`tmpfs /tmp`), `cap_drop: ALL`,
`no-new-privileges`, loopback-bound port (`REVIEWFORGE_BIND` overrides), and no
registry pull (locally built `reviewforge:latest`; CI pushes only `sha-<commit>` tags).

Mounts (see `docker-compose.yml`):

- named volume `reviewforge-data` → `/var/reviewforge` — `ReviewForge__WorkDir` is
  `/var/reviewforge/work`, so head checkouts live at `work/checkouts/<repository>/<head>`,
  local mirrors at `work/mirror/<repository>`, per-run `work/findings/{runId}.jsonl`;
  the SQLite store is `reviewforge.db` at the volume root.
  Inspect with `docker compose exec reviewforge ls /var/reviewforge/work/checkouts`;
  eviction removes idle head checkouts, not mirrors. The rootfs is read-only; only
  `/var/reviewforge`, `/home/app/.codex` and `/tmp` are writable.
- `${CODEX_AUTH_DIR:-~/.codex}` → `/home/app/.codex` — directory mount, read-write
  (atomic token rotation rewrites auth.json), owner-only because it holds a long-lived
  refresh token: `sudo chown -R 1654:1654 ~/.codex && chmod 700 ~/.codex && chmod 600 ~/.codex/auth.json`.

Container listens on 8080; compose maps host 5080 → 8080 to match the CLI default.

## Extension points (recap)

- New PR host (GitHub/GitLab): `IPullRequestSource` — pipeline untouched.
- New reasoning provider: `IChatClientFactory` (see `ChatClientFactory`).
- Prompt tuning: `ReviewForge:PromptOverridePath` → markdown file; default is the embedded
  resource `ReviewForge.Core.Reasoning.Prompts.native-review-system.md`.
- Finding identity: `DedupeKey` = ruleId + file + normalized snippet (no line numbers —
  shift-proof across force-pushes). Bot threads carry the key in ADO thread properties.
