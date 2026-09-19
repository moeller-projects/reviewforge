# AGENTS.md — ReviewForge

Guidance for coding agents working in this repository. Read this before editing.

## What this is

Automated PR review as a service (.NET 10, ASP.NET minimal API). For each submitted
Azure DevOps pull request it runs a 10-stage pipeline: fetch context → gate → clone →
classify → enrich → agent reasoning loop → validate findings → triage threads → publish (findings + summary + reviewer vote) → persist. See `README.md` for the stage table.

## Layout

```
src/
  ReviewForge.Core/            pure domain + pipeline + agent loop. NO IO adapters, NO vendor SDKs.
    Domain/                    models, ReviewGate, RunClassifier, ThreadTriage
    Analysis/                  DedupeKey (shift-proof), DiffIndex, AnchorResolver
    Reasoning/                 NativeReviewAgent, RepoReadTools, prompt building, embedded system prompt
    Pipeline/                  ReviewPipeline, Stages/ (one class per stage), CommentFormatter
    Ports/                     IPullRequestSource, IFindingStore, IGitOps, IChatClientFactory, IContextEnricher
  ReviewForge.Infrastructure/  adapters: Ado/ (ADO SDK), Git/ (LibGit2Sharp), Codex/ (OAuth file),
                               Chat/ (OpenAI/Codex clients), Persistence/ (SQLite via EF Core)
  ReviewForge.Service/         host: Endpoints, Queue/ (bounded channel + RunTracker), ReviewWorker,
                               ReviewPipelineFactory (composition root), Program.cs
  ReviewForge.Cli/             thin HTTP client (submit / status) — all logic is server-side
tests/
  ReviewForge.Testing/         shared fakes (FakePullRequestSource, FakeFindingStore, FakeGitOps,
                               FakeEnricher, FakeChatClientFactory) + ScriptedChatClient
  ReviewForge.{Core,Infrastructure,Service}.Tests/
prompts/native-review-system.md   human-editable copy; the runtime default is the embedded
                                  resource in src/ReviewForge.Core/Reasoning/Prompts/
```

## Hard rules (do not violate)

1. **Ports and adapters.** `ReviewForge.Core` must stay free of IO adapters and vendor
   SDKs. New PR host → implement `IPullRequestSource`. New reasoning provider → implement
   `IChatClientFactory`. Enrichment → `IContextEnricher` (fail-safe contract). The
   pipeline must not change for a new adapter.
2. **No engine fallback.** A failed stage fails the run; the failure is visible in run
   status. Never swallow an exception, never add a silent fallback engine or provider.
3. **Secrets from the environment only.** ADO PAT: `REVIEWFORGE_ADO_PAT`. OpenAI key:
7. **Persistence discipline.** Skipped runs (gate-terminated) are never persisted — a
   draft skip must not mark a head as reviewed.
5. **Coverage gate.** Every test project enforces ≥97% line coverage (coverlet
   `Threshold=97`) on its own SUT assembly. Any code you add must be covered or explicitly
   excluded with justification (`[ExcludeFromCodeCoverage]` is reserved for pure
   vendor-SDK wrappers like `AdoPullRequestSource`, `LibGit2SharpGitOps`, `Program.cs`).
   All logic must live in covered code.
6. **Agent sandbox.** `RepoReadTools` is read-only, rooted at the checkout, escape-proof,
   deny-regex for `.git`, `.env*`, `*.pem`, `*.key`, `secrets`. Do not add a shell tool.
7. **Persistence discipline.** Skipped runs (gate-terminated) are never persisted — a
   draft skip must not mark a head as reviewed.
8. **Codex auth file** is rewritten atomically (temp + move) on token rotation. Any mount
   or path you introduce must preserve that (directory mount, read-write).

## Conventions

- `Directory.Build.props`: `net10.0`, nullable enable, implicit usings, `LangVersion=latest`.
  Experimental APIs suppressed centrally (`MAAI001`, `OPENAI001`, `MEAI001`).
- Options are typed records/classes with DataAnnotations, validated fail-fast at startup.
  Config sections: `Ado`, `Reasoning`, `ReviewForge`. Env overrides use double underscore,
  e.g. `ReviewForge__MaxIterations=50`.
- Tests: xUnit, no mocking framework — hand-written fakes live in `ReviewForge.Testing`.
  Service tests run the real host in-process via `WebApplicationFactory<Program>` with
  fakes swapped in DI. `ScriptedChatClient` scripts the agent loop's tool calls.
- Test projects get internals via `<InternalsVisibleTo>` in the SUT csproj.
- Commits: Conventional Commits (`feat`, `fix`, `refactor`, `test`, …), imperative,
  no trailing period.

## Commands

```bash
dotnet build                                        # build everything
dotnet test                                         # full suite incl. coverage gates
dotnet test tests/ReviewForge.Core.Tests /p:CollectCoverage=true
dotnet run --project src/ReviewForge.Service        # serves http://localhost:5080
```

Service endpoints: `POST /reviews` → 202 `{runId, statusUrl}` · `GET /reviews/{runId}` ·
`GET /health`. The queue is bounded (100) with a single worker; `RunTracker` is in-memory (status is lost on restart).

## Docker

`Dockerfile` is multi-stage: restore layer (project files only) → publish → alpine
runtime (non-root `app` user, healthcheck on `/health`, read-only rootfs ready).
LibGit2Sharp and SQLite use their bundled native binaries; no `git` binary is needed.

```bash
docker compose up --build        # needs REVIEWFORGE_ADO_PAT in the environment
docker logs -f reviewforge
```

Mounts (see `docker-compose.yml`):

- `~/.codex` → `/home/app/.codex` — **read-write, directory mount**: token rotation
  rewrites `auth.json` atomically. A read-only single-file mount would break rotation.
- named volume `reviewforge-data` → `/var/reviewforge` — repo checkouts (`work/<repo>`), per-run `findings/{runId}.jsonl`, and `reviewforge.db`. Inspect with
  `docker compose exec reviewforge ls /var/reviewforge/work` (rootfs is read-only; only
  `/var/reviewforge`, `/home/app/.codex` and `/tmp` are writable).

Container listens on 8080; compose maps host 5080 → 8080 to match the CLI default.

## Extension points (recap)

- New PR host (GitHub/GitLab): `IPullRequestSource` — pipeline untouched.
- New reasoning provider: `IChatClientFactory` (see `ChatClientFactory`).
- Prompt tuning: `ReviewForge:PromptOverridePath` → markdown file; default is the embedded
  resource `ReviewForge.Core.Reasoning.Prompts.native-review-system.md`.
- Finding identity: `DedupeKey` = ruleId + file + normalized snippet (no line numbers —
  shift-proof across force-pushes). Bot threads carry the key in ADO thread properties.
