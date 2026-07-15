# Pipeline

## Purpose

Document the `Stage` interface, the 11 default stages, the data they exchange, and how to add a new stage. This is the **explanation + reference** for `reviewforge.pipeline`.

## Audience

- Maintainers extending the review pipeline (adding / removing / reordering stages).
- Maintainers debugging a stage's output (what artifact was written, what ctx was populated).

## The `Stage` interface

Every stage is a subclass of `reviewforge.pipeline.stage.Stage`:

```python
class Stage:
    name: str = "stage"

    def should_run(self, ctx: StageContext) -> bool: ...
    def run(self, ctx: StageContext) -> dict[str, Any]: ...
```

Contract:

- `name` is the stage's stable identifier (used in `run-summary.json`).
- `should_run` decides whether to skip (e.g. when a precondition is missing). Default: always run.
- `run` does the work and returns a `dict` of details. The dict is JSON-serializable (it lands in the summary).
- Failures are caught by `Stage.__call__` and recorded as `StageResult(status="failed", error=...)`. Stages do not need to wrap their work in try/except for this.

## `StageContext`

`StageContext` is the mutable per-run state passed between stages. The orchestrator builds it once; stages read from and write to it.

```python
@dataclass
class StageContext:
    cfg: Config
    artifacts: Artifacts
    state: RepoState | None
    pi: PiRunner
    metadata: dict[str, Any] = field(default_factory=dict)
    intent: dict[str, Any] | None = None
    plan: dict[str, Any] | None = None
    collected: dict[str, Any] | None = None
    digest: dict[str, Any] | None = None
    candidate: dict[str, Any] | None = None
    verified: dict[str, Any] | None = None
    severity: dict[str, Any] | None = None
    final: dict[str, Any] | None = None
    posted: dict[str, int] = field(default_factory=dict)
    skip_reason: str | None = None
    extras: dict[str, Any] = field(default_factory=dict)
    last_token_usage: dict[str, int] = field(default_factory=dict)
```

Convention:

- Only the stage that "owns" a field writes to it. Other stages read by name.
- `extras` is for stage-specific scratch that does not fit the canonical fields.
- `last_token_usage` is updated by stages that call `PiRunner.run_json` and aggregated into the run summary.

## The 11 default stages (in order)

`reviewforge.pipeline.stages.DEFAULT_PIPELINE` is the canonical review pipeline. From `stages/__init__.py`:

| # | Stage | Reads from ctx | Writes to ctx | Artifacts written |
|---|---|---|---|---|
| 1 | `FetchPrMetadataStage` | — | `metadata` | `metadata.json` |
| 2 | `PrepareRepositoryStage` | `metadata` | `state` | `commits.txt`, `raw/` |
| 3 | `BuildArtifactsStage` | `state`, `metadata` | — | `diff.patch`, `changed-files.json` |
| 4 | `ReconstructIntentStage` | `state`, `metadata` | `intent` | `intent.json` |
| 5 | `PlanContextStage` | `state`, `metadata`, `intent` | `plan` | `context-plan.json` |
| 6 | `CollectContextStage` | `plan`, `state` | `collected` | `collected-context.json` |
| 7 | `ContextDigestStage` | `collected`, `state` | `digest` | `context-digest.json` |
| 8 | `ReviewDiffStage` | `digest`, `state` | `candidate` | `candidate-findings.json` |
| 9 | `VerifyFindingsStage` | `candidate`, `state` | `verified` | `verified-findings.json` |
| 10 | `CalibrateSeverityStage` | `verified` | `severity` | `severity-findings.json` |
| 11 | `PostToAdoStage` | `severity` | `posted` | `final-findings.json`, `posted-comments.json` |

The orchestrator also calls `finalize_run_summary(...)` after the last stage, which writes `run-summary.json`.

### Stage-by-stage summary

#### 1. `FetchPrMetadataStage`

Fetches the PR JSON via `AdoClient.get_pr(include_work_item_refs=True)`. Validates the result and stores it in `ctx.metadata`. The stage is required; if it fails, the run aborts.

#### 2. `PrepareRepositoryStage`

Clones (or fetches) the repo at the PR's source commit. Uses the `GIT_ASKPASS` shim from `git/ops.py` to supply the ADO token without putting it in any visible command line. Stores the result in `ctx.state` (a `RepoState` dataclass with `repo_dir`, `base_commit`, `source_commit`, `target_commit`, `diff_text`, `files`, `range_spec`).

#### 3. `BuildArtifactsStage`

Materializes the diff to `diff.patch` and the changed-file list to `changed-files.json`. The `changed_files()` helper from `artifacts/builder` classifies each file by language and `isTest` flag.

#### 4. `ReconstructIntentStage`

Calls `pi` with the `Intent` schema. The model returns a structured intent: `pr_intent`, `changed_behaviors`, `risk_areas`. Validated by pydantic. Stored in `ctx.intent` and `intent.json`.

#### 5. `PlanContextStage`

Calls `pi` to produce a `context_plan`: which files to read, which tests to run, which searches to perform. The plan is the input to the deterministic context-collection stage. Stored in `ctx.plan` and `context-plan.json`.

#### 6. `CollectContextStage`

Reads the files and tests from the plan (using `cat` and a test-list scanner). Also runs the searches via `git grep`. Caps each file at `cfg.context_file_max_lines` (default 260) and each search at `cfg.context_search_max_matches` (default 40). Writes `collected-context.json`.

The caps live on `Config` (single source of truth), not on the process env, so the orchestrator and any out-of-band caller stay in sync.

#### 7. `ContextDigestStage`

Asks `pi` to compress the collected context into a digest the model can re-read in a single prompt. Stored in `ctx.digest` and `context-digest.json`. Token savings are significant: subsequent stages see the digest, not the raw collected text.

#### 8. `ReviewDiffStage`

The main review. Splits the diff into file-based chunks via `git.chunker.build_chunks(state, max_bytes=cfg.max_diff_bytes)`. For each chunk, calls `pi` and asks for findings. Concatenates into `ctx.candidate` and `candidate-findings.json`.

If the diff is small (under `cfg.chunk_trigger_diff_bytes`), the whole diff is sent in one call. If `cfg.disable_chunk_review` is true, the whole diff is always sent in one call.

#### 9. `VerifyFindingsStage`

For each candidate finding, asks `pi` to re-evaluate against the digest + the changed code. Findings that do not survive verification are dropped (with a `dropped` counter recorded in the stage's details). The survivors land in `ctx.verified` and `verified-findings.json`.

#### 10. `CalibrateSeverityStage`

For each verified finding, asks `pi` to recalibrate the severity (`nit` / `minor` / `major` / `blocker`) given the PR's intent and the digest. Findings with mismatched severity are updated, not dropped. Output: `ctx.severity` and `severity-findings.json`.

#### 11. `PostToAdoStage`

Copies `severity-findings.json` to `final-findings.json`. In dry-run mode, prints the final doc to stdout and records `ctx.posted = {"created": 0, "skipped": 0, "dry_run": 1}`. Otherwise calls `call_helper(cfg, "post-findings", artifacts.dir, findings=artifacts.final)` to invoke the legacy subprocess, which handles dedup, file/line mapping, and posting.

The stage always runs (even in dry-run) so the summary captures the outcome.

## `REVIEW_ONLY_PIPELINE` and `POST_ONLY_PIPELINE`

Two shorter pipelines are exposed for the CLI subcommands that do not need the full set:

```python
REVIEW_ONLY_PIPELINE = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    BuildArtifactsStage(),
    ReconstructIntentStage(),
    PlanContextStage(),
    CollectContextStage(),
    ContextDigestStage(),
    ReviewDiffStage(),
    VerifyFindingsStage(),
    CalibrateSeverityStage(),
    # No PostToAdoStage
]

POST_ONLY_PIPELINE = [
    FetchPrMetadataStage(),
    # (no other review stages; PostToAdoStage reads the input doc directly)
    PostToAdoStage(),
]
```

`run_review_only` uses the first list. `run_post_only` uses the second.

## `run_post_only`

`run_post_only(cfg, *, input_path)` is the entry point for the `post` CLI subcommand. It:

1. Validates `cfg.validate_files()` (the prompt files must exist even though we won't call them).
2. Creates the artifact directory tree.
3. Reads the input file (must be a JSON file shaped like `final-findings.json`).
4. Calls `validate_review_doc(payload)` — so a hand-edited or model-generated doc cannot post malformed findings.
5. Persists the payload as `severity-findings.json` and `final-findings.json` so `PostToAdoStage` sees the expected shape.
6. Runs the `POST_ONLY_PIPELINE`.

This is the right tool for "I already have a review, just post it".

## Orchestrator

`reviewforge.pipeline.orchestrator` exposes three top-level entrypoints:

```python
def run_full(cfg) -> RunOutcome
def run_review_only(cfg, *, output: Path | None = None) -> RunOutcome
def run_post_only(cfg, *, input_path: Path) -> RunOutcome
```

All three:

1. Build a `RunSummary` (see [`artifacts.md`](artifacts.md)).
2. Build a `StageContext`.
3. Run the appropriate `*_PIPELINE` via `run_stages`.
4. Record each stage's outcome into the summary.
5. Call `finalize_run_summary` and write `run-summary.json`.
6. Return a `RunOutcome(exit_code, summary, stages)`.

The exit code is `0` if all stages ran successfully (including the dry-run / skip cases), `1` if any stage failed.

## Schemas

`reviewforge.pipeline.schemas` defines pydantic models for each stage's JSON output. The pattern:

- `_Base` config: tolerate extra keys (`extra="ignore"`) and forbid coercion.
- `Literal` enums for severity / confidence / context-basis.
- Field validators for things like non-empty strings, max lengths, etc.

Stages use `Model.model_validate(payload)` immediately after `pi` returns. If validation fails, the stage raises and `run_stages` records the failure. The model gets a repair call only on JSON parse failures, not on schema failures (those are more fundamental — the model produced a structurally valid but semantically wrong answer, and a second ask is unlikely to fix it).

## Extending the pipeline (how-to)

### Add a stage

1. Create `reviewforge/pipeline/stages/<name>.py`:
   ```python
   class MyStage(Stage):
       name = "my_stage"
       def should_run(self, ctx):
           return ctx.<previous_field> is not None
       def run(self, ctx):
           # ... do work ...
           return {"details_key": ...}
   ```
2. Re-export it from `reviewforge/pipeline/stages/__init__.py` and add an instance to `DEFAULT_PIPELINE` at the right position.
3. Add a corresponding field to `StageContext` if it produces a structured output (or use `extras` for scratch).
4. Add a pydantic schema in `schemas.py` if the output is consumed by a later stage.
5. Add tests in `tests/test_stages.py`.

### Remove a stage

The inverse. Remove the import + the entry from `DEFAULT_PIPELINE`. Stages that read from the removed stage's output should be removed or updated to no longer read it.

### Reorder stages

Edit `DEFAULT_PIPELINE` directly. The orchestrator stops on the first failure, so a reordering that breaks a downstream stage's precondition will surface as a clear stage failure.

### Disable a stage without removing it

Override `should_run` to return `False` based on a config flag, or set the relevant `ctx` field to `None` before the stage runs. The summary still records a `status="skipped"` entry, which is useful for debugging.

## Observability

Every stage's outcome is recorded in `run-summary.json`:

```json
{
  "stages": [
    {
      "name": "review_diff",
      "status": "ok",
      "started_at": "2026-06-14T08:30:01.234Z",
      "duration_ms": 12453,
      "details": {"chunks": 3, "truncated_any": false, "findings": 12},
      "error": null,
      "token_usage": {"in": 8421, "out": 912, "total": 9333}
    }
  ]
}
```

`token_usage` is aggregated per-stage. The top-level summary also has a `finding_counts` dict and the run's `posted` counters. See [`artifacts.md`](artifacts.md#run-summary) for the full shape.
