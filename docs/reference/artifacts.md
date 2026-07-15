# Artifacts

## Purpose

Document the per-run output layout — the directory tree, the `ARTIFACT_NAMES` contract, the `RunSummary` shape, and the redaction policy. This is the **reference** for "where is X written?" and "what's in the run summary?".

## Audience

- Operators inspecting a run's output to debug a failure.
- Maintainers adding a new artifact (which means: declare it in `ARTIFACT_NAMES`, add a field to `Artifacts`, and add the write site).

## Layout

Default location: `<REVIEW_ARTIFACT_ROOT>/pr-<PR_ID>/runs/<RUN_ID>/`.

```text
<REVIEW_ARTIFACT_ROOT>/
└── pr-<PR_ID>/
    ├── latest.txt                      # points at the most recent run
    └── runs/
        └── <RUN_ID>/
            ├── run-id.txt
            ├── metadata.json
            ├── diff.patch
            ├── changed-files.json
            ├── commits.txt
            ├── intent.json
            ├── context-plan.json
            ├── collected-context.json
            ├── context-digest.json
            ├── candidate-findings.json
            ├── verified-findings.json
            ├── severity-findings.json
            ├── final-findings.json
            ├── posted-comments.json
            ├── run-summary.json
            ├── review-system.combined.md
            ├── work-items.json
            ├── threads.json
            └── raw/                      # per-stage raw Pi output (debugging)
```

When `REVIEW_ARTIFACT_DIR` is set, the runner uses that directory verbatim (no `pr-<id>/runs/<run_id>/` nesting). The `run_id` becomes the literal string `custom` and `latest.txt` is not written.

## The `ARTIFACT_NAMES` contract

`reviewforge.artifacts.manager.ARTIFACT_NAMES` is a module-level tuple of 17 names. Every well-formed run produces (or attempts to produce) all 17. The tuple order is the same as the order in which the pipeline writes them.

```python
ARTIFACT_NAMES = (
    "metadata.json",
    "diff.patch",
    "changed-files.json",
    "commits.txt",
    "intent.json",
    "context-plan.json",
    "collected-context.json",
    "context-digest.json",
    "candidate-findings.json",
    "verified-findings.json",
    "severity-findings.json",
    "final-findings.json",
    "posted-comments.json",
    "run-summary.json",
    "review-system.combined.md",
    "work-items.json",
    "threads.json",
)
```

The `Artifacts` dataclass carries the resolved path for each:

```python
@dataclass(frozen=True)
class Artifacts:
    dir: Path
    run_id: str
    metadata: Path
    diff: Path
    changed_files: Path
    commits: Path
    intent: Path
    plan: Path
    collected: Path
    digest: Path
    candidate: Path
    verified: Path
    severity: Path
    final: Path
    posted: Path
    summary: Path
    system_prompt: Path
    raw_dir: Path
    work_items: Path
    threads: Path
```

`as_dict()` returns a flat dict mapping artifact name → absolute path string. Downstream tooling (cleanup jobs, dashboards) iterates `ARTIFACT_NAMES` to find the files.

## Per-file reference

| File | Written by | Contents |
|---|---|---|
| `run-id.txt` | `Artifacts.create` | The run id (single line). |
| `metadata.json` | `FetchPrMetadataStage` | The ADO PR JSON (including work-item refs and reviewers). |
| `diff.patch` | `BuildArtifactsStage` | The unified diff from base commit to source commit. |
| `changed-files.json` | `BuildArtifactsStage` | A list of `{file, language, isTest}` for each changed file. |
| `commits.txt` | `PrepareRepositoryStage` | The commits included in the PR, one per line. |
| `intent.json` | `ReconstructIntentStage` | The reconstructed `Intent` (pydantic schema). |
| `context-plan.json` | `PlanContextStage` | The `ContextPlan` (files to read, tests to run, searches to perform). |
| `collected-context.json` | `CollectContextStage` | The collected context (file excerpts, test outputs, search hits). |
| `context-digest.json` | `ContextDigestStage` | The Pi-compressed digest of the collected context. |
| `candidate-findings.json` | `ReviewDiffStage` | The initial findings, before verification. |
| `verified-findings.json` | `VerifyFindingsStage` | Findings that survived verification. |
| `severity-findings.json` | `CalibrateSeverityStage` | Findings with calibrated severity. |
| `final-findings.json` | `PostToAdoStage` | The exact doc posted (or, in dry-run, printed to stdout). |
| `posted-comments.json` | `PostToAdoStage` | The posting result: `{created, skipped, votedWaitingForAuthor, ...}`. |
| `run-summary.json` | Orchestrator | The aggregated `RunSummary` (see below). |
| `review-system.combined.md` | (system prompt assembly) | The combined system prompt (reviewer prompt + language hint + standards file). |
| `work-items.json` | Legacy `fetch-context` | Linked work items. |
| `threads.json` | Legacy `fetch-context` | Existing PR threads (used for the dedupe scan). |
| `raw/<stage>.txt` | Stages (debugging) | Raw Pi output for a stage. Not always written. |

## Run summary

`run-summary.json` is the single best place to look when a run fails or behaves unexpectedly. It is written by the orchestrator after the last stage finishes.

### Top-level shape

```json
{
  "pr_id": "1234",
  "run_id": "20260614T083000Z-1234",
  "started_at": "2026-06-14T08:30:00.000Z",
  "finished_at": "2026-06-14T08:30:42.123Z",
  "duration_ms": 42123,
  "dry_run": false,
  "pi_model": "openai/gpt-5.5",
  "pi_session_id": "pr-1234-review-20260614T083000Z-1234",
  "pi_session_enabled": true,
  "stages": [...],
  "finding_counts": {
    "candidate": 12,
    "verified": 8,
    "posted": 6,
    "skipped": 2
  },
  "posted": {
    "created": 6,
    "skipped": 2,
    "votedWaitingForAuthor": true
  },
  "skipped_reason": null,
  "exit_code": 0,
  "artifact_dir": "/workspace/artifacts/pr-1234/runs/20260614T083000Z-1234",
  "review_language": "English"
}
```

### `stages[]`

Each entry is a `StageRecord`:

```json
{
  "name": "review_diff",
  "status": "ok",
  "started_at": "2026-06-14T08:30:15.000Z",
  "duration_ms": 12453,
  "details": {"chunks": 3, "truncated_any": false, "findings": 12},
  "token_usage": {"in": 8421, "out": 912, "total": 9333}
}
```

`status` is one of `ok`, `skipped`, `failed`. On `failed`, the stage's `error` field contains the exception class and message. (Note: `error` is on `StageResult` but is not surfaced in the `RunSummary`'s per-stage `details` — see [Adding fields](#adding-fields).)

`token_usage` is `{}` if the stage did not call `pi` (e.g. `FetchPrMetadataStage`).

### Redaction

The `RunSummary` and its stage records are written to disk and may be uploaded to a CI artifact store. They never contain:

- The ADO token (`ADO_AUTH_TOKEN` / `ADO_MCP_AUTH_TOKEN` / `ADO_API_KEY`).
- The OpenAI / Anthropic key.
- The `OPENAI_API_KEY` or any provider key in the env.

The redaction is policy-based: the summary is built from the orchestrator's structured state, not from the env. As long as the orchestrator does not put a secret in a `details` dict, the summary is safe.

The stage-level `token_usage` is fine to expose — it is just integer counts.

## `findings` shape

The findings array in `final-findings.json` (and the intermediate `*-findings.json` files) has the shape:

```json
{
  "summary": "One-paragraph review summary.",
  "findings": [
    {
      "severity": "major",
      "confidence": "high",
      "title": "Token in log",
      "message": "The ADO token is logged at INFO on every call. ...",
      "file": "src/log.ts",
      "line": 10,
      "contextBasis": "diff-only",
      "evidence": {
        "contextFilesRead": ["src/log.ts"],
        "searches": ["grep -n 'ADO_AUTH_TOKEN' src/"]
      },
      "suggestion": "Replace console.log with a redacted logger."
    }
  ]
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `severity` | `"nit"` / `"minor"` / `"major"` / `"blocker"` | yes | Calibrated by the severity stage. |
| `confidence` | `"high"` / `"medium"` / `"low"` | yes | The model's self-reported confidence. |
| `title` | string | yes | Short summary, fits in one line. |
| `message` | string | yes | Body of the finding, may span multiple lines. |
| `file` | string | optional | Path relative to the repo root. May have a leading `/` (normalized by `dedupe_key`). |
| `line` | integer | optional | 1-indexed line in the new file. Used for `threadContext`. |
| `contextBasis` | string | yes | `"diff-only"`, `"surrounding-code-read"`, or `"full-module-review"`. Used by `REQUIRE_CONTEXT_FOR`. |
| `evidence` | object | optional | The model's evidence trail: `contextFilesRead` and `searches` lists. |
| `suggestion` | string | optional | A concrete fix suggestion. |

## The `raw/` directory

Stages that need to capture raw `pi` output for debugging (e.g. when a parse fails) write to `raw/<stage>.txt`. The convention is not enforced; it's an opportunistic area. The `raw/` directory is not part of `ARTIFACT_NAMES` because not every run populates it.

## Adding a new artifact (how-to)

1. Add the name to `ARTIFACT_NAMES` in `artifacts/manager.py` (in pipeline order — keep it sorted by when the file is written).
2. Add a field to the `Artifacts` dataclass.
3. Add a `Path` assignment in `Artifacts.create` (e.g. `my_artifact=root / "my-artifact.json"`).
4. Add a mapping in `Artifacts.as_dict` so consumers can find it.
5. Add a write site in the appropriate stage or helper. Use `artifacts.builder.write_json` for JSON, `Path.write_text` for text.
6. If the artifact needs to be summarized in `run-summary.json`, add a field to `RunSummary` and populate it from the stage's `details` dict in `orchestrator._record_results`.
7. Update the per-file reference table above.

## Cleaning up

The Docker named volume `reviewforge-artifacts` persists across container runs. Operators should occasionally prune old runs:

```bash
# Inside the container
find /workspace/artifacts/pr-*/runs -maxdepth 1 -mindepth 1 -mtime +30 -type d -exec rm -rf {} +
```

Or set `REVIEW_ARTIFACT_VOLUME_NAME` to a fresh value in CI to start clean. There is no built-in TTL or quota.
