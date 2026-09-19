# reviewforge (.NET)

Automated PR review as a service: fetches pull-request context from Azure DevOps, reviews
the change with an agent (Microsoft Agent Framework), verifies linked work-item acceptance
criteria, triages existing threads, posts findings, and sets the reviewer vote.

## Architecture

```
src/
  ReviewForge.Core/           pure domain + pipeline + agent loop (no IO adapters)
    Domain/                   models, ReviewGate, RunClassifier, ThreadTriage
    Analysis/                 DedupeKey (shift-proof), DiffIndex, AnchorResolver
    Reasoning/                NativeReviewAgent, tools, collector, prompt building
    Pipeline/                 ReviewPipeline + 10 stages, CommentFormatter, telemetry
    Ports/                    IPullRequestSource, IFindingStore, IGitOps, IChatClientFactory, IContextEnricher
  ReviewForge.Infrastructure/ adapters: ADO (SDK), LibGit2Sharp, Codex OAuth, SQLite (EF Core), OpenAI
  ReviewForge.Service/        ASP.NET host: REST ingest + queue + worker + OpenTelemetry
  ReviewForge.Cli/            thin client for the service (submit / status)
tests/
  ReviewForge.Testing/        shared fakes + ScriptedChatClient
  ReviewForge.{Core,Infrastructure,Service}.Tests/
```

## The 10-stage pipeline

| #  | Stage              | What it does                                                                                                                                               |
|----|--------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1  | fetch-pr-context   | PR metadata, linked work items (with acceptance criteria), changed files, threads, PAT identity, prior run                                                 |
| 2  | review-gate        | skips drafts and "same head, no new human comments" — graceful early exit                                                                                  |
| 3  | prepare-repository | clone/reuse checkout, checkout head, unified diff → DiffIndex                                                                                              |
| 4  | classify-run       | re-fetches threads (new comments?), full vs follow-up, pending human replies                                                                               |
| 5  | enrich-context     | optional code-review-graph payload into the context store (fail-safe)                                                                                      |
| 6  | execute-reasoning  | agent loop: repo read tools + record_finding/record_uncertainty/task_done, sliding-window compaction, iteration cap, findings streamed to per-run `findings/{runId}.jsonl` files |
| 7  | validate-findings  | re-anchors via snippet (AnchorResolver), downgrades unverifiable/out-of-diff anchors to general comments                                                   |
| 8  | triage-threads     | answers/resolves/reopens threads per agent decision, auto-resolves vanished findings, flags unanswered threads                                             |
| 9  | publish-findings   | inline or general comments, summary comment with AC verdicts, reviewer vote **-5 (waiting for author)** when findings/AC-unmet/unanswered exist            |
| 10 | persist-run        | run + finding keys to SQLite (feeds gate + dedupe); skipped runs are never persisted                                                                       |

## Run it

```bash
export REVIEWFORGE_ADO_PAT=...            # ADO personal access token (never in config files)
# provider openai-codex: ~/.codex/auth.json must exist (OAuth, auto-refresh + atomic persist)
# provider openai:       export OPENAI_API_KEY=...

dotnet run --project src/ReviewForge.Service          # serves http://localhost:5080

dotnet src/ReviewForge.Cli/bin/Debug/net10.0/reviewforge.dll submit \
  --org my-org --project my-project --repo my-repo --pr 1234
dotnet src/ReviewForge.Cli/bin/Debug/net10.0/reviewforge.dll status --run-id <guid>
```

Endpoints: `POST /reviews` → 202 `{runId, statusUrl}` · `GET /reviews/{runId}` · `GET /health` ·
`POST /reviews/discover` → 200 sweep report.

## API docs (opt-in)

Off by default (the API has no auth — the schema must not be published unless enabled). Set
`ApiDocs:Enabled=true` to expose:

- `GET /openapi/v1.json` — OpenAPI 3.x document (title/version/description from `ApiDocs:Title`/`ApiDocs:Version`).
- `GET /scalar/v1` — Scalar reference UI.

## Configuration

`src/ReviewForge.Service/appsettings.json` — typed options with DataAnnotations validation,
fail-fast at startup. PAT and API keys come from the environment only.

- `ReviewForge:ReasoningEffort` — reasoning effort for the review agent (`None`, `Low`,
  `Medium`, `High`, `ExtraHigh`). Omit it (or leave unset) to keep the provider default. The
  Codex endpoint may ignore or restrict effort per model — verify against the deployed model.

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
short name), `Creators` (empty = allow all; matches creator id or name), `MaxEnqueuesPerSweep`
(default 20), `SweepInterval` (a `hh:mm:ss` interval; unset/null disables the background sweep
worker). Skip reasons are checked in order: draft, target branch, creator, no linked work items,
already-reviewed head. The per-run review gate remains the final dedupe net — a sweep enqueue is
only a candidate; the gate decides whether a run actually proceeds.

## Runtime concurrency and checkout storage

`ReviewForge:WorkerCount` controls the number of concurrent queue workers. Each review
holds its per-head checkout lease until the run finishes. `ReviewForge:Checkout` controls
idle checkout eviction: `Enabled`, `MaxAge`, `MaxCheckoutsPerRepo`, and `SweepInterval`.
Eviction removes old or over-cap head checkouts but never mirrors, and skips checkouts
currently held by a review.

## Tests and coverage gate

```bash
dotnet test
dotnet test tests/ReviewForge.Core.Tests /p:CollectCoverage=true
```

Every test project enforces **at least 97% line coverage** via coverlet (`Threshold=97`)
on its own SUT assembly. Vendor-only adapters such as `AdoPullRequestSource`,
`LibGit2SharpGitOps`, and `Program.cs` are excluded by design; all application logic
remains covered.

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
