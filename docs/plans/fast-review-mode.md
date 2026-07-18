# Plan: Fast Review Mode (Single Pi Call)

## Goal
Add an opt-in `FAST_REVIEW` mode that runs the entire Pi-driven portion of the review pipeline — intent reconstruction, context planning, context collection, context digest, diff review, finding verification, and severity calibration — in **one agent call**, returning a richer JSON document. The non-Pi stages (metadata fetch, repo preparation, AC coverage, posting) remain unchanged.

## Decisions

| Open question | Decision |
|---|---|
| **O1** — Diff-size threshold / fallback | No threshold and no fallback. When `FAST_REVIEW=1`, the review is performed in a single Pi call regardless of diff size. If the call fails, the pipeline fails; there is no automatic fallback to the default 12-stage pipeline. |
| **O2** — Flag fast-path reviews in output | No. The response and artifacts are intentionally indistinguishable from a full-pipeline review. No `fast_path` flag is added to findings, comments, or run summary. |
| **O3** — Cache storage for fast-mode results | Use the same artifact store as the current pipeline (`artifacts/pr-<id>/runs/<run-id>/`). The raw rich response may be written as an auxiliary file for debugging, but the canonical contract files (`intent.json`, `context-plan.json`, `collected-context.json`, `context-digest.json`, `candidate-findings.json`, `verified-findings.json`, `severity-findings.json`, `final-findings.json`) are synthesized from the response. |

## Scope

### In scope
- New `FAST_REVIEW` config flag and `--fast-review` CLI option.
- New pipeline variant `FAST_REVIEW_PIPELINE`.
- New `FastReviewStage`.
- New prompt file `prompts/fast-review-system.md`.
- New Pydantic schema `FastReviewResult` plus supporting schemas.
- Synthesizing existing artifact files from the single response so `PostToAdoStage` and `AcceptanceCriteriaCoverageStage` need no changes.
- Orchestrator wiring in `run_full` and `run_review_only`.
- Tests and documentation.

### Out of scope
- Removing or changing the default 12-stage pipeline.
- Changing the ADO posting contract (markers, dedupe key, comment format).
- Changing chunking behavior in the default pipeline.
- Agent-driven context collection replacing the deterministic `CollectContextStage` in the default pipeline.
- Automatic fallback from fast mode to the default pipeline.

## Risk tier
**High** — touches orchestrator, config, CLI, schemas, prompts, and artifact contract. Additive, but introduces a new execution path for the same public behavior.

## Requirements

1. **R1** When `FAST_REVIEW=1` (or `--fast-review`), the pipeline must run `FetchPrMetadataStage → PrepareRepositoryStage → BuildArtifactsStage → FastReviewStage → AcceptanceCriteriaCoverageStage → PostToAdoStage`.
2. **R2** `FastReviewStage` must make exactly one `PiRunner.run_json` call.
3. **R3** The single call must produce a JSON object matching the new `FastReviewResult` schema.
4. **R4** The `findings` array in the response is treated as already verified and calibrated; it is written directly to `final-findings.json` and `severity-findings.json`.
5. **R5** Existing artifact files must be synthesized from the response so downstream consumers (including `run_post_only`) see the expected layout.
6. **R6** The fast path must reuse existing ADO posting and stale-comment reconciliation unchanged.
7. **R7** AC coverage must still run in fast mode and append uncovered findings to `final-findings.json`.
8. **R8** When `FAST_REVIEW=0`, behavior must be identical to today.
9. **R9** Fast mode must be opt-in only; the default pipeline remains unchanged.

## Acceptance criteria

**AC1** — Fast path produces valid findings
> GIVEN a PR with `FAST_REVIEW=1`
> WHEN `run_full` completes
> THEN `final-findings.json` contains a valid `ReviewDoc` and every finding has `severity` ∈ {blocker, major, minor, nit}, non-empty `title`, and non-empty `message`.

**AC2** — Fast path is one Pi call
> GIVEN `FAST_REVIEW=1`
> WHEN `FastReviewStage.run` executes
> THEN `ctx.pi.run_json` is called exactly once.

**AC3** — Backward compatibility
> GIVEN `FAST_REVIEW=0`
> WHEN `run_full` executes
> THEN the existing 12-stage pipeline runs and the output is byte-for-byte equivalent to before.

**AC4** — Idempotency preserved
> GIVEN `FAST_REVIEW=1` and findings already posted
> WHEN `run_full` runs again
> THEN `PostToAdoStage` skips duplicates using the existing `prb:` marker logic.

**AC5** — AC coverage still appends
> GIVEN `FAST_REVIEW=1` and a linked work item with uncovered AC
> WHEN `AcceptanceCriteriaCoverageStage` runs
> THEN a general-thread finding is appended to `final-findings.json`.

**AC6** — Coverage gate maintained
> GIVEN new `FastReviewStage` code
> WHEN `pytest` runs
> THEN `coverage` ≥ 95%.

**AC7** — No fast-path markers in output
> GIVEN `FAST_REVIEW=1`
> WHEN comments are posted and artifacts are written
> THEN there is no field, flag, or metadata indicating the review used fast mode.

## Proposed rich JSON schema

The agent must return a single JSON object with this shape:

```json
{
  "intent": {
    "pr_intent": "string",
    "changed_behaviors": ["string"],
    "risk_areas": ["string"]
  },
  "context_summary": {
    "files_read": [{"path": "string", "reason": "string"}],
    "searches_run": [{"query": "string", "reason": "string"}],
    "tests_inspected": ["string"],
    "notes": "string"
  },
  "review_summary": {
    "summary": "string",
    "notes": "string"
  },
  "verification_summary": {
    "summary": "string",
    "notes": "string"
  },
  "findings": [
    {
      "severity": "blocker|major|minor|nit",
      "title": "string",
      "message": "string",
      "file": "string|null",
      "line": "integer|null",
      "confidence": "high|medium|low|null",
      "contextBasis": "diff-only|surrounding-code-read|full-module-review|null",
      "suggestion": "string|null",
      "evidence": {
        "changedLines": [1, 2],
        "contextFilesRead": ["src/foo.py"],
        "whyNewInThisPr": "string",
        "whyNotIntentional": "string"
      }
    }
  ],
  "statistics": {
    "findings_count": 0,
    "by_severity": {"blocker": 0, "major": 0, "minor": 0, "nit": 0},
    "files_read_count": 0,
    "searches_run_count": 0,
    "tests_inspected_count": 0
  }
}
```

### Pydantic schemas

- `FastReviewResult` — top-level container.
- `ContextSummary` — files, searches, tests, notes.
- `ReviewSummary` — summary + notes.
- `VerificationSummary` — summary + notes.
- `ReviewStatistics` — counts and by-severity map.
- Reuse existing `Intent` and `Finding` schemas.

## Artifact synthesis

`FastReviewStage` writes the following files so the rest of the pipeline sees a normal layout:

| Artifact | Source |
|---|---|
| `intent.json` | `response.intent` |
| `context-plan.json` | `files_to_read`, `searches_run`, `tests_inspected` from `context_summary` |
| `collected-context.json` | `context_summary` (content pointers and notes) |
| `context-digest.json` | `context_summary.notes` + `statistics` |
| `candidate-findings.json` | `response.findings` with `review_summary.summary` |
| `verified-findings.json` | `response.findings` with `verification_summary.summary` |
| `severity-findings.json` | `response.findings` with `review_summary.summary` |
| `final-findings.json` | `response.findings` with merged `review_summary` + `verification_summary` |
| `review-system.combined.md` | Existing combined system prompt (written by `BuildArtifactsStage`) |

Optionally, the raw response may be written to `artifacts/pr-<id>/runs/<run-id>/fast-review.json` for debugging, but this is not part of the stable `ARTIFACT_NAMES` contract.

## Pipeline variant

```python
FAST_REVIEW_PIPELINE: list[Stage] = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    BuildArtifactsStage(),
    FastReviewStage(),
    AcceptanceCriteriaCoverageStage(),
    PostToAdoStage(),
]
```

## Implementation tasks

```text
tasks:
- id: F-01
  title: Add FAST_REVIEW config flag and CLI option
  estimate: S
  depends_on: []
  done_when: Config has `fast_review: bool`, CLI accepts `--fast-review`, env var `FAST_REVIEW=1` works, and existing tests still pass.

- id: F-02
  title: Define FastReviewResult Pydantic schema
  estimate: S
  depends_on: []
  done_when: `src/reviewforge/pipeline/schemas.py` contains `FastReviewResult`, `ContextSummary`, `ReviewSummary`, `VerificationSummary`, `ReviewStatistics`, and tests validate them.

- id: F-03
  title: Create fast-review-system prompt
  estimate: M
  depends_on: [F-02]
  done_when: `prompts/fast-review-system.md` instructs the agent to read context via tools and return only the rich JSON schema, with examples.

- id: F-04
  title: Implement FastReviewStage
  estimate: L
  depends_on: [F-02, F-03]
  done_when: Stage makes one Pi call, validates response, writes all synthesized artifacts listed above, and has unit tests.

- id: F-05
  title: Add FAST_REVIEW_PIPELINE and orchestrator wiring
  estimate: M
  depends_on: [F-01, F-04]
  done_when: `src/reviewforge/pipeline/stages/__init__.py` exports `FAST_REVIEW_PIPELINE`, `run_full`/`run_review_only` branch on `cfg.fast_review`, and `run_post_only` is unaffected.

- id: F-06
  title: Test fast path end-to-end
  estimate: M
  depends_on: [F-04, F-05]
  done_when: `tests/test_fast_review.py` covers success, invalid JSON, schema mismatch, and artifact synthesis; coverage gate passes.

- id: F-07
  title: Update docs and changelog
  estimate: S
  depends_on: [F-05]
  done_when: `docs/reference/pipeline.md`, `docs/reference/configuration.md`, and `CHANGELOG.md` describe the fast path, limitations, and schema.

dependencies:
- F-02 → F-03: schema shapes the prompt examples
- F-02 → F-04: stage validates against schema
- F-03 → F-04: stage uses the prompt
- F-01 → F-05: orchestrator needs the flag
- F-04 → F-05: pipeline needs the stage
- F-04 → F-06: tests exercise the stage
- F-05 → F-06: tests exercise the orchestrator branch
- F-05 → F-07: docs reference the final behavior

critical_path:
- F-02 → F-03 → F-04 → F-05 → F-06 → F-07
```

## Risks

- **RISK-1** — Prompt engineering. Getting the agent to reliably output the full rich JSON in one call may require iteration. The system prompt must be explicit about the required schema and the prohibition on prose.
- **RISK-2** — Context limits. Large diffs plus requested context reads may exceed the model window. This is accepted per O1; no fallback is implemented. Users with very large PRs should stay on the default pipeline.
- **RISK-3** — Observability. Synthesizing intermediate artifacts hides the actual reasoning path. The optional raw `fast-review.json` can mitigate this during debugging.
- **RISK-4** — Coverage gate. New stage is large; tests must be thorough to keep coverage ≥ 95%.
- **RISK-5** — Agent tool misuse. The agent may read files outside the repo or fail to read enough context. The prompt must instruct it to stay within the repo and use `read`/`grep` tools.

## Open questions

None remaining. Decisions are captured above.

## Next action

Begin implementation with task **F-02** (schema definition) and **F-03** (prompt creation) in parallel; then proceed to **F-04** (stage implementation) once the schema is stable.
