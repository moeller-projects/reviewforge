# Plan 01 — Review-Filter Stage (Fact-Checker Pass)

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/review-filter-stage` · **Size:** M

## Context

The pipeline today: `FetchPrMetadataStage → PrepareRepositoryStage → EnrichWithCrgStage → ExecuteReasoningEngineStage → ValidateAnchorsStage → PostToAdoStage` (registered in `src/reviewforge/pipeline/stages/__init__.py`). Every finding the engine produces and anchor validation keeps gets posted to Azure DevOps. There is no second-pass quality gate on finding *content* — only on finding *location* (`ValidateAnchorsStage`).

OpenCodeReview runs a dedicated **review-filter** LLM pass between review and output: a fact-checker that sees only the diff and the produced comments, and removes only comments the diff *proves* wrong. Source: `internal/config/template/prompts/review_filter_task_system.md`. Its load-bearing insight — asymmetric error cost:

> Keeping an incorrect comment costs a reviewer a few seconds. Removing a correct comment silently destroys a real finding. So when evidence falls short of proof, approve.

Expected effect: fewer false positives posted to PRs; smaller triage load for the planned backend.

## Goal

Add a `FilterFindingsStage` between `ValidateAnchorsStage` and `PostToAdoStage` that asks the model to verdict each surviving finding `keep`/`drop`, moves dropped findings into `discarded_findings`, and writes a `filter-report.json` artifact. Fail-open: any filter error keeps all findings.

## Non-goals

- No filtering inside the reasoning engines themselves (stage-level, engine-agnostic).
- No severity re-calibration (that is `CalibrateSeverityStage`'s job in the legacy flow).
- No human/backend triage — this is automated pre-post filtering only.

## Design

```text
ValidateAnchorsStage → FilterFindingsStage → PostToAdoStage
                            │
                            ├─ input:  ctx.review_result.findings (post-anchor),
                            │          diff text (ctx.state.diff_text or artifacts.diff)
                            ├─ Pi call: filter system prompt + diff + findings JSON
                            ├─ output: {"verdicts":[{"index":N,"keep":bool,"reason":str}]}
                            ├─ dropped → review_result.discarded_findings + filter-report.json
                            └─ any error → keep everything, log warning, record status
```

Placement after `ValidateAnchorsStage` is deliberate: anchor validation is deterministic and cheap, so the LLM filter only spends tokens on findings that can actually be posted.

## Implementation steps

### 1. Config (`src/reviewforge/config.py`)

Add to `Config`:

| Field | Type | Default | Env alias |
|---|---|---|---|
| `filter_enabled` | `bool` | `True` | `FILTER_ENABLED` (`"0"/"false"` disables) |
| `filter_prompt_path` | `str` | `"prompts/review-filter-system.md"` | `FILTER_PROMPT_PATH` |
| `filter_max_findings` | `int` | `50` | `FILTER_MAX_FINDINGS` (batch size per filter call) |
| `filter_diff_max_bytes` | `int` | `120000` | `FILTER_DIFF_MAX_BYTES` |

Follow the existing parsing pattern used for `anchor_policy` / `pi_session_enabled`. Document all four in `docs/reference/environment-variables.md`.

### 2. Filter prompt (`prompts/review-filter-system.md`, new file)

Adapted from OCR `review_filter_task_system.md` (Apache-2.0). Full text:

```markdown
You are a fact-checker for code review comments.

These review comments come from an agent that could invoke tools to read the
full repository. You can see only the unified diff of the pull request and the
comments it produced. Anything you cannot see, the agent may well have seen.

Your task is narrow: remove only the comments that this diff **proves** to be
factually wrong. You are not judging whether a comment is useful,
well-prioritized, or worth a reviewer's time.

The two mistakes available to you are not equally bad:

- Keeping an incorrect comment costs a reviewer a few seconds of attention.
- Removing a correct comment silently destroys a real finding. It never
  reaches anyone, and nobody learns that it was dropped.

So when your evidence falls short of proof, keep the comment. "Suspicious",
"I cannot verify this", "low value", "the flagged code looks fine to me", and
"I would not have raised this" all mean keep.

## Input

You receive:
1. The unified diff of the pull request.
2. A JSON array of findings. Each finding has: `index` (integer), `title`,
   `observation`, `impact`, `recommendation`, `severity`, `file`, `line`.

## Output

Return ONLY a JSON object, no prose, no code fences:

{"verdicts": [{"index": 0, "keep": true, "reason": "not disproven by diff"}]}

- One verdict per input finding, referenced by its `index`.
- `keep: false` requires a `reason` stating which diff hunk proves the
  finding wrong. Vague reasons are not allowed for drops.
- `keep: true` may use a short reason or an empty string.
```

### 3. Schema (`src/reviewforge/pipeline/schemas.py`)

```python
class FilterVerdict(_Base):
    index: int
    keep: bool
    reason: str = ""

    @model_validator(mode="after")
    def _drop_requires_reason(self) -> "FilterVerdict":
        if not self.keep and not self.reason.strip():
            raise ValueError("dropping a finding requires a reason")
        return self


class FilterVerdicts(_Base):
    verdicts: list[FilterVerdict] = Field(default_factory=list)
```

Add both to `__all__`. Add `filterRemoved: int = Field(default=0, ge=0)` and `filterInvocationCount: int = Field(default=0, ge=0)` to `ReviewMetrics`.

### 4. Stage (`src/reviewforge/pipeline/stages/filter_findings.py`, new file)

```python
"""Second-pass fact-checker that removes provably wrong findings."""
from __future__ import annotations

import json
from typing import Any

from ...artifacts.builder import write_json
from ...runlog import info, warning
from ..schemas import DiscardedFinding, FilterVerdicts, validate_payload
from ..stage import Stage, StageContext


class FilterFindingsStage(Stage):
    name = "filter_findings"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(getattr(ctx.cfg, "filter_enabled", False))

    def run(self, ctx: StageContext) -> dict[str, Any]:
        # See detailed flow below.
        ...
```

Flow inside `run()`:

1. If `ctx.review_result is None` or `not ctx.review_result.findings` → return `{"kept": 0, "dropped": 0, "status": "no_findings"}`.
2. Load diff text: `ctx.state.diff_text` if present, else read `ctx.artifacts.diff`. Truncate to `cfg.filter_diff_max_bytes` using the same `_utf8_prefix` pattern as `reasoning/single_pi.py` (import or duplicate the helper into a shared util — prefer moving it to `reviewforge/git/ops.py` or a new `reviewforge/text.py` and updating the import in `single_pi.py`).
3. Build the findings payload: `[{"index": i, "title": ..., "observation": ..., "impact": ..., "recommendation": ..., "severity": ..., "file": ..., "line": ...}]` from `ctx.review_result.findings`.
4. Batch: if `len(findings) > cfg.filter_max_findings`, split into batches; one Pi call per batch; merge verdicts. Track `metrics.filterInvocationCount += batches`.
5. Pi call: use the **same per-PR session** as the engine (PiRunner already supports session reuse — pass the runner from `ctx.extras` if the engine stored it there under a documented key, e.g. `ctx.extras["pi_runner"]`; if absent, construct a fresh `PiRunner(ctx.cfg)`). Send: system prompt from `cfg.filter_prompt_path` + user message containing the diff and the findings JSON. Request JSON output to a temp file, following the pattern the engines use.
6. Parse + validate with `validate_payload(FilterVerdicts, raw)`. On `json.JSONDecodeError` or `ValidationError` → **fail-open**: log `warning("filter pass unparsable; keeping all findings")`, write `filter-report.json` with `{"status": "error", ...}`, return.
7. Apply verdicts:
   - Verdicts referencing out-of-range indices are ignored (log warning).
   - Findings with no verdict are **kept** (missing verdict ≠ drop).
   - Dropped findings: remove from `review_result.findings`; append one `DiscardedFinding(reason=f"filter: {verdict.reason}", category="filter")` **per dropped finding** (keep reasons traceable; do not aggregate into a single count row).
8. Update metrics: `metrics.filterRemoved = dropped`.
9. Write artifacts:
   - `filter-report.json`: `{"status": "ok"|"error"|"disabled", "kept": N, "dropped": M, "verdicts": [{"index", "keep", "reason", "title", "file", "line"}]}` — include finding identity per verdict so the report is self-describing.
   - Rewrite `ctx.artifacts.review_result` with the updated `review_result.model_dump(by_alias=True, exclude_none=False)` (same pattern as `ValidateAnchorsStage`).
10. Re-run projection if `ctx.final` exists: mirror `ValidateAnchorsStage` — rebuild `ctx.final["findings"]` from the retained findings via `pipeline.projection` and `write_json(ctx.artifacts.final, ctx.final)`. Check `projection.py` for the exact function name (`project_findings` or similar) and reuse it; do not hand-roll the projection.
11. Return `{"kept": N, "dropped": M, "batches": B}`.

### 5. Register the stage (`src/reviewforge/pipeline/stages/__init__.py`)

Import `FilterFindingsStage` and insert **after** `ValidateAnchorsStage()` in `DEFAULT_PIPELINE`, `REVIEW_ONLY_PIPELINE`, `FAST_REVIEW_PIPELINE`, `FAST_REVIEW_REVIEW_ONLY_PIPELINE`. Not in `POST_ONLY_PIPELINE` (posting a previously generated review must not re-filter). Add to `__all__`. Update the module docstring stage list.

### 6. Engine session handoff

In `src/reviewforge/reasoning/single_pi.py` (and `multi_stage.py` if it constructs its own runner): after a successful engine run, store the runner: `ctx.extras["pi_runner"] = runner`. This lets the filter reuse the warm session (system prompt + diff already in context → the filter turn is cheap). If the runner is absent (e.g. `review --no-post` replays), the stage constructs its own — both paths must work.

## Tests (`tests/test_filter_findings.py`, new)

Mock `PiRunner` (follow mocking patterns in `tests/test_stages.py` / `tests/test_review_runner.py`):

1. `test_skips_when_disabled` — `filter_enabled=False` → `should_run()` False.
2. `test_no_findings_noop` — empty findings → status `no_findings`, no Pi call.
3. `test_drops_proven_wrong` — verdict `keep:false` with reason → finding removed, `discarded_findings` gains `category="filter"` entry with the reason, `filter-report.json` written, metrics updated.
4. `test_missing_verdict_keeps_finding` — verdict list shorter than findings → unverdicted findings retained.
5. `test_out_of_range_index_ignored` — verdict index 99 with 2 findings → warning, both kept.
6. `test_unparsable_response_fails_open` — Pi returns garbage → all findings kept, report status `error`.
7. `test_drop_without_reason_rejected` — schema validation rejects `{"keep": false, "reason": ""}` → fail-open path.
8. `test_batching` — `filter_max_findings=2`, 5 findings → 3 Pi calls, merged verdicts.
9. `test_projection_updated` — `ctx.final["findings"]` matches retained set after run.
10. `test_session_reused_when_available` — runner taken from `ctx.extras["pi_runner"]` when present.

## Docs

- `docs/architecture/pipeline.md`: add `FilterFindingsStage` to the `DEFAULT_PIPELINE` list and one paragraph on semantics (fail-open, post-anchor placement, batching).
- `docs/reference/artifacts.md`: document `filter-report.json`.
- `docs/reference/schemas.md`: document `FilterVerdicts`.
- `docs/reference/environment-variables.md`: the four new variables.
- `CHANGELOG.md`: `## Unreleased` entry.

## Verification

```bash
python -m pytest tests/test_filter_findings.py -v
python -m pytest                              # full suite green
FILTER_ENABLED=1 reviewforge review --dry-run # manual smoke on a real PR
```

## Acceptance criteria

1. Stage registered in all four review pipelines, after anchor validation.
2. Dropped findings appear in `discarded_findings` with their individual reasons and in `filter-report.json` with full identity.
3. Any filter-side failure (Pi error, bad JSON, schema violation) keeps 100% of findings and is visible in the report + logs.
4. No finding is ever dropped without a model-supplied reason referencing the diff.
5. Full test suite passes.
