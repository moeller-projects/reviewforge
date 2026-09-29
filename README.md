# reviewforge (.NET)

Automated PR review as a service: fetches pull-request context from Azure DevOps, reviews
the change with an agent (Microsoft Agent Framework), verifies linked work-item acceptance
criteria, triages existing threads, posts findings, and sets the reviewer vote.

## Architecture

```
src/
  ReviewForge.Core/           pure domain + pipeline + agent loop (no IO adapters)
    Domain/                   models, ReviewGate, RunClassifier, ThreadTriage, FailureBackoff
    Analysis/                 DedupeKey (shift-proof), DiffIndex, AnchorResolver, ShardPlanner,
                              homoglyph analyzer, LineEditEngine
    Reasoning/                NativeReviewAgent, RepoReadTools, prompt building, RuleBook,
                              embedded prompts (native-review-system.md, fix-pass-system.md)
    AutoFix/                  fixer registry + deterministic fixers, fix-pass prompt, applied-fix rows
    Workspaces/               RepoCheckoutPool (per-head checkout leases, idle eviction)
    Pipeline/                 ReviewPipeline + stages, CommentFormatter, telemetry
    Ports/                    IPullRequestSource, IFindingStore, IGitOps, IChatClientFactory,
                              IContextEnricher, IReviewQueue, IWorkspaceFs
  ReviewForge.Infrastructure/ adapters: Ado/ (ADO SDK + retry policy), Git/ (LibGit2Sharp + operation
                              scheduler), Codex/ (OAuth file, streaming client), Chat/ (OpenAI/Codex
                              clients + LLM governor), Persistence/ (SQLite via EF Core), Filesystem/
  ReviewForge.Service/        ASP.NET host: Endpoints, Queue/ (ReviewQueue+RunTracker, InFlightClaims),
                              Security/ (API-key filter), Logging/ (per-run JSONL), hosted workers
                              (ReviewWorker, DiscoverySweepWorker, CheckoutEvictionWorker,
                              ShellReaperService), ReviewPipelineFactory (composition root), OpenTelemetry
  ReviewForge.AppHost/        Aspire orchestration for the dev loop (dashboard + OTLP injection)
  ReviewForge.Cli/            thin client for the service (submit / status)
tests/
  ReviewForge.Testing/        shared fakes + ScriptedChatClient
  ReviewForge.{Core,Infrastructure,Service}.Tests/
  ReviewForge.Architecture.Tests/  assembly-boundary tests (deliberately no coverlet gate)
```

## The review pipeline

10 numbered stages plus two inserts (7.2 auto-fix, 7.5 begin-run); every stage is a class in
`Core/Pipeline/Stages/` and a failed stage fails the run.

| #  | Stage              | What it does                                                                                                                                               |
|----|--------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1  | fetch-pr-context   | PR metadata, linked work items (with acceptance criteria), changed files, threads, PAT identity, prior run                                                 |
| 2  | review-gate        | skips drafts and "same head, no new human comments" — graceful early exit                                                                                  |
| 3  | prepare-repository | clone/reuse checkout, checkout head, unified diff → DiffIndex                                                                                              |
| 4  | classify-run       | re-fetches threads (new comments?), full vs follow-up, pending human replies                                                                               |
| 5  | enrich-context     | optional code-review-graph payload into the context store (fail-safe)                                                                                      |
| 6  | execute-reasoning  | agent loop: repo read tools + record_finding/record_uncertainty/task_done, sliding-window compaction, iteration cap, findings streamed to per-run `findings/{runId}.jsonl` files |
| 7  | validate-findings  | re-anchors via snippet (AnchorResolver), downgrades unverifiable/out-of-diff anchors to general comments                                                   |
| 7.2| auto-fix-findings  | suggestion-only fixes (off by default): deterministic rule fixers + author-commanded `/rf fix` passes; with the default null verifier the deterministic path performs **zero** checkout writes |
| 7.5| begin-run          | persists the in-flight run shell (run row + finding keys, `Success=false`) so an interrupted run stays visible and later stages backfill durable rows; finalized by PersistRunStage or the startup ShellReaperService |
| 8  | triage-threads     | answers/resolves/reopens threads per agent decision, auto-resolves vanished findings, flags unanswered threads                                             |
| 9  | publish-findings   | inline or general comments, summary comment with AC verdicts, reviewer vote **-5 (waiting for author)** when findings/AC-unmet/unanswered exist; clean runs get `ReviewForge:CleanRunVote` (default NoResponse) |
| 10 | persist-run        | finalizes the run row (Success, CompletedAt); skipped runs are never persisted                                                                            |

## Run it

```bash
export REVIEWFORGE_ADO_PAT=...            # ADO personal access token (never in config files)
# docker compose additionally requires ADO_ORG_URL and ADO_PROJECT (no internal defaults).
export REVIEWFORGE_API_KEYS=...           # comma-separated API keys for the /reviews endpoints
# provider openai-codex: ~/.codex/auth.json must exist (OAuth, auto-refresh + atomic persist).
#   auth.json holds a long-lived refresh token; keep the directory owner-only:
#     chmod 700 ~/.codex && chmod 600 ~/.codex/auth.json
#   (the service tightens the file to 0600 on every persist and load, but the
#   directory mode is yours to set). Production: prefer a dedicated service
#   account over a developer's personal ~/.codex.
# provider openai:       export OPENAI_API_KEY=...

dotnet run --project src/ReviewForge.Service          # serves http://localhost:5080

dotnet src/ReviewForge.Cli/bin/Debug/net10.0/reviewforge.dll submit \
  --org my-org --project my-project --repo my-repo --pr 1234 --api-key ...
dotnet src/ReviewForge.Cli/bin/Debug/net10.0/reviewforge.dll status --run-id <guid> --api-key ...
```

Endpoints (all `/reviews*` require the `X-Api-Key` header; keys are configured via the
`REVIEWFORGE_API_KEYS` environment variable, comma- or semicolon-separated for rotation):
`POST /reviews` → 202 `{runId, statusUrl}`, 400 on validation errors, 401 without a valid
key, 409 when a review for the same PR is already in flight (the conflicting run id is in
the body), 429 over the submit limit (`Api:SubmitPermitLimit` per `Api:SubmitWindowSeconds`,
default 10/60s), 503 when the bounded queue is full ·
`GET /reviews/{runId}` (status: the bounded in-memory tracker first, then queued-row and
store read-through — with `ReviewForge:QueueMode=Sqlite` queued and finalized runs stay
visible across host restarts; with the default `Memory` mode in-flight status is lost on
restart) · `POST /reviews/discover` · `GET /health` (store-backed, unauthenticated) ·
`GET /alive` (liveness, unauthenticated). Rate limiting runs before API-key auth (auth is
an endpoint filter, the limiter is middleware), so rejected requests still consume rate
budget. The limiter partitions by `X-Api-Key` when keys are configured, by remote IP
otherwise — deploying behind a reverse proxy requires forwarded-headers support, otherwise
every key-less client shares the proxy's single partition.

## Dev loop: Aspire vs Docker Compose

**Local dev — Aspire dashboard.** `aspire run` (or `dotnet run --project src/ReviewForge.AppHost`)
starts the service with a live dashboard for traces, metrics, and logs. Set the three secret
parameters first:

```bash
cd src/ReviewForge.AppHost
dotnet user-secrets set "Parameters:ado-pat" "<ado-pat>"
dotnet user-secrets set "Parameters:openai-api-key" "<openai-key>"
dotnet user-secrets set "Parameters:api-key" "<reviewforge-api-key>"
```

The dashboard URL is printed on startup; `WorkDir` is the system temp dir
(`Path.GetTempPath()/reviewforge`). Aspire injects the
`OTEL_EXPORTER_OTLP_*` variables automatically, so no OTLP endpoint is ever hardcoded.

**Production — Docker Compose.** `docker compose up -d` runs exactly as before (no telemetry
export). To add the standalone Aspire dashboard as an opt-in sink:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://dashboard:18889 docker compose --profile observability up -d
# UI: http://localhost:18888 (localhost-bound; front with an authenticated proxy for remote access)
```

To ship telemetry to a real backend instead, set `OTEL_EXPORTER_OTLP_ENDPOINT` (+ `_HEADERS`)
in the host environment and skip the profile. The dashboard profile is a documented option,
not the default: its UI is unauthenticated unless fronted, and it ingests all telemetry
including scoped log properties.

## API docs (opt-in)

Off by default (the API schema must not be published unless explicitly enabled). Set
`ApiDocs:Enabled=true` to expose — startup then warns unless `Api:Keys`/`REVIEWFORGE_API_KEYS`
are configured, because the docs describe the `/reviews*` surface:

- `GET /openapi/v1.json` — OpenAPI 3.x document (title/version/description from `ApiDocs:Title`/`ApiDocs:Version`).
- `GET /scalar/v1` — Scalar reference UI.

## Configuration

`src/ReviewForge.Service/appsettings.json` — typed options with DataAnnotations validation,
fail-fast at startup. PAT and API keys come from the environment only.

- `ReviewForge:ReasoningEffort` — reasoning effort for the review agent (`None`, `Low`,
  `Medium`, `High`, `ExtraHigh`). Omit it (or leave unset) to keep the provider default. The
  Codex endpoint may ignore or restrict effort per model — verify against the deployed model.
- `ReviewForge:Store:JournalMode` — `Wal` (default) or `Delete`. WAL lets readers proceed
  during writes and tolerates a power loss losing only the last transaction — safe for this
  dedupe/audit store. WAL requires POSIX advisory locks: keep `StoreConnectionString` on a
  local disk (the shipped container volume is fine); on network filesystems use `Delete`.
- `ReviewForge:QueueMode` — `Memory` (default, in-memory channel) or `Sqlite` (durable rows on
  the store's database file). Sqlite mode survives host restarts: queued runs are re-claimed
  after the claim TTL, `GET /reviews/{runId}` reads through the queue row (`Queued`) and the
  store row (`Completed`/`Failed`), and expired claims are counted by
  `reviewforge.queue.reclaimed_total`. In-flight PR claims remain in-memory for now — a
  restarted run is re-acquired idempotently at dequeue.
- `ReviewForge:Sharding` — map-reduce sharding for large diffs. `Enabled` (default `false`)
  turns it on; when the planned shard count is at least two, stage 6 runs one agent per
  shard concurrently and merges findings into the single run collector. `ShardMaxChars`
  (default 30000) is the cumulative diff budget per shard — a file larger than the budget
  gets a shard of its own and is never split across shards. `MaxShards` (default 8) caps the
  shard count: overflow falls back to the legacy truncated single-agent path, never a run
  failure (`reviewforge.shard.fallback_total` counts it). `ShardConcurrency` (default 2)
  bounds concurrent shard agents and is also bounded by the LLM governor's global cap.
  Runs report their shard count under the `shards` metric tag; per-shard durations land in
  `reviewforge.shard.duration_ms`. Accepted v1 limitation: cross-file findings confined to
  no single shard can be missed — every shard still sees the full file manifest and per-file
  diff headers, so interface-level reasoning is preserved.
- `Reasoning:FollowUpModel` — optional cheaper/faster model for the Fast tier: follow-up
  reviews and `/rf fix` passes route to it; full reviews keep `Reasoning:Model`. Must carry
  the same provider routing prefix (`openai-codex:…` with `openai-codex:…`); mismatches fail
  startup validation. Unset (default) aliases the Fast tier to the full model — identical
  behavior, zero config. Token metrics carry a `model` tag, so per-tier cost splits out
  without new series.
- `ReviewForge:TrivialDiffSkipEnabled` — default true. Iterations whose post-exclusion diff
  adds zero reviewable lines (lockfile-only churn, deletions-only) with no open threads get
  a clean vote without an LLM call; counted by `reviewforge.reviews.trivial_total`. The
  deterministic homoglyph analyzer still runs. Set false to restore the always-run behavior.
- `Reasoning:MaxConcurrentRequests` / `Reasoning:GovernorAcquireTimeoutSeconds` — the
  process-wide LLM governor bounds concurrent provider HTTP requests (across both model
  tiers) so `WorkerCount × iterations` cannot burst the provider into 429s. Unset cap
  defaults to `ReviewForge:WorkerCount × 2`; an acquisition timeout fails the run with a
  visible `LlmGovernorTimeoutException` (counted by `reviewforge.llm.governor.timeout_total`).
  Watch `reviewforge.llm.governor.wait_ms` before tightening the cap.
- `ReviewForge:WorkerCount` — concurrent queue workers (validated 1–64; unset defaults to
  `processorCount/2` clamped to 2–8).
- `ReviewForge:CleanRunVote` — reviewer vote on clean runs (no findings, all AC met, no
  unanswered threads): `NoResponse` (default) | `Approved` | `ApprovedWithSuggestions` |
  `None` (leave the vote untouched).
- `ReviewForge:StaleShellMinutes` — 10 by default. At startup, in-flight run shells
  (persisted by begin-run, never finalized — a crash between stages 7.5 and 10) older than
  this are reaped and finalized as failures so a crashed head can be re-reviewed after a
  bounded window.
- `ReviewForge:Retention:Days` / `MinRunsPerPr` — 30 / 5. Old store runs are pruned at the
  discovery-sweep tail (at most hourly); the latest completed run and the last
  `MinRunsPerPr` runs of a PR are always kept.
- `ReviewForge:GitMaxConcurrency` — dedicated LibGit2Sharp operation scheduler bound
  (unset defaults to `processorCount/2` clamped to 2–4).
- `ReviewForge:TargetedFetchEnabled` — default true; targeted/partial fetches into the
  shared mirror so prepare-repository skips full origin fetches.
- `ReviewForge:OtlpEnabled` — opt-in switch enabling OTLP export without an env endpoint;
  the standard `OTEL_EXPORTER_OTLP_*` variables alone also turn export on (per-signal
  variants included). Never hardcode an endpoint in configuration.
- `RepoReadTools:GrepMaxMs` / `GrepMaxLines` — aggregate wall-clock (default 10 s) and
  line (default 200k) budgets for one agent Grep call; the call aborts with a truncation
  marker when either is hit.
- Diff budgets: `ReviewForge:MaxDiffChars` (200k) / `MaxDiffCharsPerFile` (40k) and
  `MaxDiffBytes` (4 MiB) / `MaxDiffBytesPerFile` (256 KiB); oversized diffs are truncated
  with a marker. `ReviewForge:DiffExcludeGlobs` replaces the default exclusion set
  (lockfiles, generated code) when set.

## Auto-fix (suggestion-only, off by default)

Every fix ReviewForge produces is an ADO ` ```suggestion ` block the PR author applies
with one click. ReviewForge **never** writes to the PR branch, never pushes, and never
opens pull requests; the PAT keeps comment-only permissions.

Two fix sources, gated by `AutoFix` configuration (env overrides use `AutoFix__…`):

- **Deterministic** — a validated finding whose rule has a registered, enabled fixer
  (v1: `homoglyph/mixed-script-identifier`, `homoglyph/confusable-keyword`,
  `bash.unquoted-vars`, `bash.set-e-missing`, `py.mutable-default-arg`,
  `docker.add-vs-copy`) gets a pure-C# proposal. This path performs **zero checkout
  writes**.
- **Commanded** — the PR author replies `/rf fix` on any thread (human, bot, or a
  ReviewForge finding). Only commands from the PR author, newer than the last completed
  run's comment watermark, on active file-anchored threads trigger a constrained agent
  fix pass: hash-anchored line edits, a one-file writable set, no shell, no findings
  tools. Its writes are applied, captured, and reverted before the stage ends.

```json
"AutoFix": {
  "Enabled": false,                 // master switch; false = byte-identical pipeline
  "AllowedAuthors": [],             // immutable creator ids only (display names never match); empty = disabled
  "AllowedRuleIds": [],             // intersected with the fixer registry
  "PublishMode": "Suggestion",      // the only supported mode (write modes are reserved)
  "MaxFixesPerRun": 3,              // shared budget, deterministic fixes first
  "EnableThreadFixCommands": false, // the '/rf fix' thread command
  "FixPassMaxIterations": 8         // iteration cap for one commanded fix pass
}
```

Fixes are published as suggestions; human acceptance is the verification step.

Safety properties pinned by tests: fixed findings stay in the accepted set (their keys
stay current, so triage never auto-resolves their threads); commanded suggestion threads
carry no dedupe property (invisible to triage); only the PR author's last-comment
commands trigger; command replies are deduplicated against retry; every fix is labeled,
and AI-drafted fixes are marked as such.

## Findings verification (challenge stage, off by default)

`VerifyFindings` (env overrides use `VerifyFindings__…`) adds an adversarial stage between
validation and auto-fix: one bounded, tool-free Fast-tier request tries to *disprove* each
accepted finding from a deterministic ±15-line file slice around its anchor. It can only
subtract findings — never add or rewrite — and fails open: any verifier failure keeps the
findings. Cost is one request of ≤24k prompt chars per run (~1–3% of a typical review).

```json
"VerifyFindings": {
  "Enabled": false,       // master switch; false = byte-identical pipeline
  "MaxFindings": 20,      // highest-severity first; the rest pass unverified
  "ContextLines": 15,     // file context quoted around each anchor
  "TimeoutSeconds": 60,   // shared by the request and its single retry
  "MaxPromptChars": 24000 // hard prompt cap
}
```

The per-rule rejection counter (`reviewforge.findings.verifier_rejected_total`) is the
false-positive-rate metric. Deterministic `homoglyph/*` findings and trivial-diff runs skip
the verifier entirely.

## Rulebook

Reviews use embedded general, performance, security, and language rule packs. Packs
activate from changed-file extensions, path patterns, or repository root marker files.
Set `ReviewForge:RuleSetsPath` to a directory of JSON packs to replace or extend embedded
packs. Repeated override files for the same pack are processed in filename order, with
later rules replacing earlier rules by ID; extension packs must use unique rule IDs.
Findings must use an active rule id. Use `general.other` for a verified issue that has no
more specific active rule.

## Org-wide discovery

`POST /reviews/discover` sweeps every active pull request across all org projects/repositories,
filters them, and enqueues the interesting ones (up to `Discovery:MaxEnqueuesPerSweep`). The
200 response is a `DiscoveryReport`:

```json
{
  "candidates": 5,
  "interesting": 2,
  "enqueued": [{ "org": "...", "project": "...", "repositoryId": "...", "prId": 1 }],
  "skipped": [{ "pr": { "org": "...", "...": "..." }, "reason": "draft" }]
}
```

`Discovery` options: `TargetBranches` (default `["main","develop"]`, case-insensitive on branch
short name), `Creators` (matches creator id or name; a **required allowlist** when
`SweepInterval` is set — startup fails fast with an empty list unless
`AllowAllCreators=true` explicitly opts into reviewing PRs from any author, external
contributors included), `MaxEnqueuesPerSweep` (default 20), `MaxDegreeOfParallelism`
(default 4, validated 1–16), `SweepInterval` (a `hh:mm:ss` interval; unset/null disables
the background sweep worker), `FailureBackoffBase` (default 30 min) / `FailureBackoffMax`
(default 8 h) — a head whose recent runs keep failing is skipped until a doubling backoff
elapses, and `WarmupEnabled` (default false) — when on, each accepted enqueue pre-fetches
its head commits into the shared git mirror (`WarmupMaxPerSweep` = 5 per sweep,
`WarmupConcurrency` = 2) so the run's prepare-repository stage skips the origin fetch;
warmups are best-effort (failures are logged and counted by
`reviewforge.discovery.warmup.total`) and acquire durations are tagged with `warmed` so the
win is measurable. Skip reasons are checked in order: draft, target branch, creator, no
linked work items, already-reviewed head, head failing (backoff), enqueue cap, review
already in flight, and queue full. The per-run review gate remains the final dedupe net — a
sweep enqueue is only a candidate; the gate decides whether a run actually proceeds.

## Runtime concurrency and checkout storage

`ReviewForge:WorkerCount` controls the number of concurrent queue workers (default
`processorCount/2` clamped to 2–8). The ingest queue has a fixed capacity of 100; a full
queue rejects submissions immediately with HTTP 503 (and discovery records a `queue full`
skip). Queue depth is exported as `reviewforge.queue.depth`, and rejected enqueues as
`reviewforge.queue.rejected_total`. Each review holds its per-head checkout lease until the
run finishes. `ReviewForge:Checkout` controls idle checkout eviction: `Enabled`, `MaxAge`,
`MaxCheckoutsPerRepo`, and `SweepInterval`. Eviction removes old or over-cap head checkouts
but never mirrors, and skips checkouts currently held by a review.

## Observability model

Signals and where they land:

- **Traces** — `ActivitySource` `ReviewForge` (a `review.run` span with nested `stage.*`
  spans; discovery-enqueued runs link back to the `discovery.sweep` span).
- **Metrics** — `Meter` `ReviewForge`: run lifecycle (`reviewforge.reviews.*`), stage
  latency (`reviewforge.stage.duration_ms`), queue gauges/rejections, claims, LLM tokens,
  agent iterations / `task_done` misses, findings accepted/rejected/posted, ADO latency /
  failures, discovery sweeps, enrichment failures, checkout evictions.
- **Logs** — structured, with per-run scope properties `RunId`/`PrId`/`Org`/`Project`/
  `RepositoryId`/`HeadSha`/`Stage` (OBS-2); the OTLP log exporter is env-driven like the
  other signals (`OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` or the common endpoint).
  Per-run correlation: filter console/OTLP output by the `RunId` log scope.

Backends:

- **Dev** — the Aspire dashboard (see "Dev loop").
- **Prod (optional)** — the compose `observability` profile runs a standalone Aspire
  dashboard. For another backend, set `OTEL_EXPORTER_OTLP_ENDPOINT` (+ `_HEADERS`) to an
  OTLP collector supported by the platform.
- **Repository-owned dashboards and searches** — none. Operators must use the configured OTLP
  backend's native queries and dashboards. The structured run fields above remain the
  correlation contract.

Rollout gates: green `dotnet test` and inert instruments (no exporter) → 24h staging
log-volume check → traces/metrics/logs arrive in the configured backend → a synthetic failure
can be located by `RunId` and `Stage`.

## Tests and coverage gate

```bash
dotnet test
dotnet test tests/ReviewForge.Core.Tests /p:CollectCoverage=true
```

Every production-code test project enforces **at least 95% line coverage** via coverlet
(`Threshold=95`) on its own SUT assembly across all supported operating systems.
Vendor-only adapters such as `AdoPullRequestSource`, `LibGit2SharpGitOps`, and `Program.cs` are
excluded by design; all application logic remains covered. `ReviewForge.Architecture.Tests` is
the deliberate exception: it validates assembly boundaries (NetArchTest) and covers no
production code, so it carries no coverlet threshold.

## Extension points

- **New PR host** (GitHub, GitLab): implement `IPullRequestSource` — pipeline untouched.
- **New reasoning provider**: implement `IChatClientFactory` (see `ChatClientFactory`).
- **Enrichment (CRG/MCP)**: implement `IContextEnricher`, register in DI — fail-safe contract.
- **Prompt tuning**: `ReviewForge:PromptOverridePath` points at a markdown file; the embedded
  default lives in `src/ReviewForge.Core/Reasoning/Prompts/native-review-system.md`.
- **Finding identity**: `DedupeKey` = ruleId + file + normalized snippet (no line numbers —
  shift-proof across force-pushes). Bot threads carry the key in ADO thread Properties.

## Security constraints

- Agent filesystem is read-only, rooted at the checkout, escape-proof, deny-regex for
  `.git`, `.env*`, `*.pem`, `*.key`, `secrets`. No shell tool exists.
- ADO writes happen only in stages 8–10. PAT is never logged.
- Codex auth file is rewritten atomically (temp + move) on token rotation.
- No engine fallback: a failed stage fails the run (exit/visible in status), never silently.
