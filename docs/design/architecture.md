# Architecture

## Purpose

Explain the major components of `reviewforge`, how data flows between them on a single review run, and the invariants the system maintains. This is the explanation-mode companion to the [`package-guide.md`](../reference/package-guide.md) index.

## Audience

A new maintainer who already knows Python and Azure DevOps. They want to understand "how does it all fit together" before reading module-level docs.

## Top-level architecture

The package has three layers, separated by purpose:

```text
┌──────────────────────────────────────────────────────────────────────────┐
│  Operator layer (PowerShell wrappers + src/reviewforge/*.py shims)               │
│  - run.ps1, run-open-prs.ps1 (common.psm1, build.ps1)                    │
│  - python -m reviewforge, python -m reviewforge, reviewforge.ado.cli             │
│    - Responsibility: Docker orchestration, env forwarding, secrets in      │
│      --env-file. No application logic.                                      │
└──────────────────────────────────────────────────────────────────────────┘
                                │ docker run / python -m
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  CLI layer (reviewforge.cli)                                        │
│  - argparse, subcommands (review, post, open-prs, validate-config,       │
│    discover)                                                              │
│  - Translates ConfigError into a friendly stderr message.                │
└──────────────────────────────────────────────────────────────────────────┘
                                │ cli.<subcommand>(args) → cfg
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  Orchestration layer (reviewforge.pipeline.orchestrator)             │
│  - run_full / run_review_only / run_post_only                            │
│  - Wires Config + Artifacts + PiRunner into a StageContext.              │
│  - Runs the Stage list, records outcomes, writes run-summary.json.       │
└──────────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  Stage layer (reviewforge.pipeline.stages.*)                        │
│  - 12 explicit Stage subclasses (see pipeline.md)                        │
│  - Each reads/writes to StageContext, returns a dict of details.         │
└──────────────────────────────────────────────────────────────────────────┘
                                │ subprocess.run(...)
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  LLM subprocess (the `pi` CLI)                                            │
│  - Pure read-only: no ADO tokens in env, no write tools.                 │
│  - Returns JSON to stdout; runner parses / repairs as needed.            │
└──────────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  External: Azure DevOps REST, the local git clone                         │
└──────────────────────────────────────────────────────────────────────────┘
```

## Data flow on a single review run

For `run_full`, the production path is:

```text
1. CLI builds Config with one shared engine resolver.
2. orchestrator.run_full(cfg)
   → validates required files
   → creates the per-run artifact directory
   → builds StageContext and PiRunner
3. FetchPrMetadataStage
   → deterministic ADO metadata, work items, comments, and threads
4. PrepareRepositoryStage
   → clone/fetches the PR, computes RepoState, diff, changed files, and commits
5. ExecuteReasoningEngineStage
   → selects single_pi by default
   → deterministically reduces oversized diff context
   → makes one logical Pi reasoning invocation
   → validates ReviewResult and writes the canonical result plus compatibility projections
6. PostToAdoStage
   → validates the projected postable findings
   → posts through the Python ADO helper, or prints on DRY_RUN
7. The orchestrator records stage timing, tokens, invocation counts, and runtime metrics
   in run-summary.json.
```

`multi_stage` is not part of the default path. It remains an explicit engine
selection for debugging, benchmarking, regression comparison, and fallback.

## Key abstractions

### `Config` (immutable)

`reviewforge.config.Config` is a `@dataclass(frozen=True)` containing every tunable: org, project, repo, pr_id, the ADO token, the model, the artifact paths, the language, the session flags, the dry-run flag, the severity thresholds, etc.

Two constructors:

- `Config.from_env()` — read everything from the process env (no CLI). Used by tests and library callers.
- `Config.from_sources(cli, env=None)` — read from a merged env (process env + optional `.env`) and then apply CLI overrides on top. This is what the CLI uses.

Config is the **only place** that reads `os.getenv` in application code. The one exception is `PiRunner._scrub_ado_env`, which needs the raw env so it can strip the ADO tokens before launching the subprocess.

### `Stage` (single unit of work)

```python
class Stage:
    name: str = "stage"

    def should_run(self, ctx: StageContext) -> bool: ...
    def run(self, ctx: StageContext) -> dict[str, Any]: ...
```

A stage owns a name, decides whether to run, runs, and returns a dict of details. The orchestrator captures timing and exceptions. The dict lands in `run-summary.json` under `stages[].details`.

`StageContext` is the mutable shared state passed between stages. It has strongly-typed slots for each stage's output (`intent`, `plan`, `collected`, `digest`, `candidate`, `verified`, `severity`, `final`) plus an `extras` dict for stage-specific scratch space.

### `Artifacts` (per-run output paths)

`reviewforge.artifacts.manager.Artifacts` is a frozen dataclass with one field per output file. The set of fields is fixed (`ARTIFACT_NAMES` is a module-level tuple); the orchestrator and stages treat it as a stable contract. See [`artifacts.md`](../reference/artifacts.md).

### `PiRunner` (LLM subprocess wrapper)

Owns one concern: launch `pi` as a subprocess, capture JSON output, repair on failure, and surface token usage. Session reuse is the key cost-saver: a multi-stage review reuses one Pi session so the model keeps the diff and prior context between calls. See [`ai-runner.md`](../reference/ai-runner.md).

### `AdoClient` (thin REST wrapper)

`reviewforge.ado.client.AdoClient` is a minimal bearer-token REST client. It exposes only the verbs the reviewer needs (`get_pr`, `get_threads`, `create_thread`, `vote`, plus generic `get`/`post`/`put` for the work-items and connection-data endpoints). No retry, no rate-limit logic, no SDK dependencies — just `urllib.request` with a JSON body and a 60-second timeout. See [`ado-integration.md`](../reference/ado-integration.md).

## Why these design choices

A few decisions are not obvious from reading the code. They live here so future maintainers don't undo them.

- **Why a separate `cli.py` module?** The Docker image and CI still invoke `reviewforge.ado.cli` as a subprocess. The script cannot import the package without path manipulation, so we keep a thin subprocess-friendly shim. The shim's job is purely the CLI surface; all logic is in the package. See [`ado-integration.md`](../reference/ado-integration.md#legacy-shim).

- **Why pydantic for stage outputs?** The model occasionally produces malformed JSON (missing fields, wrong severity strings). Pydantic gives clear, actionable validation errors immediately, instead of letting bad values silently propagate. See [`pipeline.md`](../reference/pipeline.md#schemas).

- **Why file-based prompts, not inline strings?** The system prompt is ~30 KB. Keeping it in a file makes it diffable, testable, and editable without rebuilding the container. The path is `Config.review_prompt_path`, configurable per env.

- **Why deduplicate by SHA-1 of (file, line, severity, title, message)?** Field-name noise from the model (e.g. inconsistent casing, reordered keys, decorative fields) would defeat string equality. A 12-char SHA-1 prefix is short enough to read in a thread, unique enough to be stable across reruns, and intentionally excludes `confidence`, `suggestion`, and `evidence` so minor model variation does not change the key.

- **Why a `Stage` interface, not just functions?** Each stage needs timing, error capture, and skip support. A class with `__call__` gives a uniform call site in `run_stages` and a uniform `StageResult` shape in the summary. New stages drop in as `class MyStage(Stage): name = "my_stage"; def run(self, ctx): ...`.

## Failure modes the design accounts for

| Failure | Mitigation |
|---|---|
| `.env` file is missing or malformed | `parse_dotenv` returns `{}` for missing files; malformed lines are silently skipped. `Config.from_sources` falls back to env / defaults. |
| ADO returns 4xx / 5xx | `AdoClient._request` logs the URL, method, and response body to stderr, then re-raises. The orchestrator catches `SystemExit` from any source as a stage failure. |
| Pi returns invalid JSON | `PiRunner.run_json` strips Markdown fences first; if the result still fails to parse, it retries once in the same session asking the model to "return only JSON". |
| Stage raises an unexpected exception | The `Stage.__call__` wrapper records `status="failed"`, the exception class+message in `error`, and the duration; `run_stages` short-circuits on the first failure. |
| Process is killed mid-run | The `try/finally` in `run_stages` is not used; partial artifacts remain on disk for forensic inspection. The next run is idempotent because posting uses markers. |
| User passes `-DryRun` | `PostToAdoStage.should_run` still returns `True` (so the summary captures the intent) but `cfg.dry_run=True` short-circuits the post; the final JSON is printed to stdout. |
| Two stages want to set the same `StageContext` field | Convention: only the stage that "owns" that field writes to it. Stages read earlier stages' fields by name. There is no schema enforcement on `ctx` itself. |

## Where to look next

- Adding a stage: [`pipeline.md`](../reference/pipeline.md#extending-the-pipeline)
- Tuning AI cost: [`ai-runner.md`](../reference/ai-runner.md#session-reuse)
- Changing the posting format: [`ado-integration.md`](../reference/ado-integration.md#posting-format)
- Reading a run's output: [`artifacts.md`](../reference/artifacts.md)
