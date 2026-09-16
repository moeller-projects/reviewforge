# Plan 03 — Semantic File Grouping for Chunked Reviews

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/semantic-grouping` · **Size:** L

## Context

Large diffs are split today by `build_chunks()` in `src/reviewforge/git/chunker.py`: greedy file-by-file packing into byte-bounded chunks (`CHUNK_TRIGGER_DIFF_BYTES`, default 200000; `DISABLE_CHUNK_REVIEW`). Chunk boundaries are purely mechanical — a handler and its test land in different chunks whenever byte math says so. The `single_pi` engine (`src/reviewforge/reasoning/single_pi.py`) reviews each chunk separately (schema `ChunkResult`) and synthesizes whole-PR summaries (`ChunkSynthesis`).

Consequence: cross-file findings (broken contracts, missing updates in call sites, i18n variants out of sync) are structurally impossible when related files are split apart.

OpenCodeReview groups files **semantically** before dispatch (sources: `internal/agent/grouping.go`, `internal/config/template/prompts/grouping_task_system.md`): one cheap LLM call over file *metadata only* (path, status, +/- counts — never diff content) returns bundles like `{label, files}`; each bundle is reviewed in one shared conversation. Guards: max 10 files/group, token-budget split, coverage reconciliation, deterministic fallback on any failure.

## Goal

Add a grouping step between chunking and per-chunk review: when chunking triggers, files are first clustered semantically (LLM), then packed into byte-bounded chunks *within* group boundaries. Related files are reviewed together; mechanical packing remains as fallback and as the budget enforcer.

## Non-goals

- No parallel/concurrent chunk review (OCR fans out goroutines; reviewforge's Pi session is sequential — keep it).
- No change to the non-chunked path (small diffs stay single-call).
- No change to `ChunkResult`/`ChunkSynthesis` schemas.

## Design

```text
build_chunks (today):        files ──greedy bytes──► chunks

build_grouped_chunks (new):  files ──LLM grouping (metadata only)──► groups[{label, files}]
                                                                     │ reconcile: every file exactly once
                                                                     │ split: >10 files, >byte budget
                                                                     ▼
                                                             chunks (group-coherent, byte-bounded)
                                                             any failure ──► build_chunks (current behavior)
```

Chunk identity gains a `label` so per-chunk prompts can say *"these files belong together: <label>"* and the model can exploit the relationship.

## Implementation steps

### 1. Config (`src/reviewforge/config.py`)

| Field | Type | Default | Env alias |
|---|---|---|---|
| `grouping_mode` | `str` | `"auto"` | `GROUPING_MODE` — `auto` (LLM when ≥4 files), `llm` (always attempt), `bytes` (never; current behavior) |
| `grouping_max_files` | `int` | `10` | `GROUPING_MAX_FILES` |
| `grouping_prompt_path` | `str` | `"prompts/grouping-system.md"` | `GROUPING_PROMPT_PATH` |

Document in `docs/reference/environment-variables.md`.

### 2. Grouping prompt (`prompts/grouping-system.md`, new file)

Adapted from OCR `grouping_task_system.md` (Apache-2.0). Full text:

```markdown
You are a file grouping assistant for code review. Group changed files into
semantically related clusters that should be reviewed together.

Files in the same group typically:
- Belong to the same module or feature
- Have producer/consumer relationships (interface and implementation,
  function and call site, schema and migration)
- Are i18n/config variants of the same resource
- Are a change and its tests
- Share a directory and work together on a single concern

Each file in the list is prefixed with a zero-based index in brackets, e.g.
`[0] MODIFIED   path/to/file (+12/-3)`. Refer to files by that integer index,
never by path.

Rules:
- Every file index must appear in exactly one group.
- A group may contain 1 file if it is unrelated to others.
- Maximum {max_files} files per group.
- Give each group a short `label` naming the shared concern (2–6 words).
- Output ONLY a JSON array, no prose, no code fences:
  [{"label": "user auth flow", "files": [0, 3, 7]}]
```

### 3. Schema (`src/reviewforge/pipeline/schemas.py`)

```python
class FileGroup(_Base):
    label: str
    files: list[int] = Field(default_factory=list)

    @field_validator("label")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("label must be a non-empty string")
        return v
```

Add to `__all__`. Reconciliation is code, not schema (see below).

### 4. Grouper module (`src/reviewforge/git/grouper.py`, new file)

```python
"""Semantic file grouping for chunked reviews.

Grouping strategy ported from alibaba/open-code-review (Apache-2.0),
internal/agent/grouping.go. Grouping is a best-effort optimisation,
never a correctness gate: any failure falls back to byte packing.
"""
from __future__ import annotations

from dataclasses import dataclass

from .chunker import DiffChunk, build_chunks
from .ops import RepoState, run_git


@dataclass(frozen=True)
class LabeledChunk:
    """A DiffChunk plus the semantic label of its group ("" for fallback)."""
    chunk: DiffChunk
    label: str = ""


def reconcile_groups(raw_groups: list[dict], file_count: int, max_files: int) -> list[list[int]]:
    """Enforce: every index in exactly one group; ≤ max_files per group.

    - Unknown/out-of-range indices: dropped from their group.
    - Duplicate assignments: first group wins.
    - Unassigned files: each gets its own solo group (appended in order).
    - Oversized groups: split into max_files-sized sub-groups preserving order.
    Returns index lists only (labels carried by caller).
    """


def split_by_budget(groups: list[list[int]], state: RepoState, max_bytes: int) -> list[list[int]]:
    """Split any group whose combined per-file diffs exceed max_bytes.

    Per-file diff sizes come from the same `git diff -- <file>` calls
    build_chunks makes; cache them so the final packing does not re-run git.
    Single file larger than max_bytes stays a solo group (truncation handled
    downstream by build_chunks semantics — reuse that code path).
    """


def build_grouped_chunks(state: RepoState, max_bytes: int, groups: list[tuple[str, list[int]]]) -> list[LabeledChunk]:
    """Pack each group's files into byte-bounded chunks (group-coherent).

    Reuses the packing loop from build_chunks but never merges across
    groups. Extract the shared packing logic from chunker.build_chunks
    into a private `_pack(files, file_diffs, max_bytes)` in chunker.py and
    call it from both — do not duplicate the truncation logic.
    """
```

### 5. Grouping call site (`src/reviewforge/reasoning/single_pi.py`)

Where the engine currently decides to chunk (search for `build_chunks` / `DISABLE_CHUNK_REVIEW` / `chunk_trigger_diff_bytes`):

1. Compute `file_count = len(state.files)`.
2. Attempt LLM grouping when: `cfg.grouping_mode == "llm"` or (`cfg.grouping_mode == "auto"` and `file_count >= 4`):
   - Build the metadata listing: `[{i}] {STATUS}   {path} (+{ins}/-{del})` — status/insertions/deletions from `git diff --numstat` + `--name-status` over `state.range_spec` (add a helper in `git/ops.py`; cache the result).
   - One Pi call in the **same session** (session reuse — cheap turn): system prompt from `cfg.grouping_prompt_path` with `{max_files}` substituted, user message = the listing. JSON output to temp file, existing engine pattern.
   - Parse → `validate_payload` against a `GroupingResult` wrapper (`{"groups": [FileGroup]}` — add schema) or bare-list-tolerant parsing; on any error → log warning, fall back to `build_chunks`.
   - `reconcile_groups` → `split_by_budget` → `build_grouped_chunks`.
3. Fallback path (`bytes` mode or any grouping failure): `build_chunks` exactly as today, `label=""`.
4. Per-chunk prompt: when `label` is non-empty, prepend to the chunk instruction:

   ```text
   These {n} files were grouped as one review unit: "{label}".
   Review them together — cross-file inconsistencies, missing updates, and
   broken contracts within this unit are first-class findings.
   ```

5. Merging: unchanged — per-chunk `ChunkResult`s merge as today; `ChunkSynthesis` unchanged.
6. Artifact: write `groups.json` — `{"mode": "llm"|"bytes-fallback", "groups": [{"label", "files": [paths], "chunk_count"}], "fallback_reason": str|null}` via `write_json`.
7. Metrics: `ReviewMetrics.chunkCount` already exists — set from final chunk list. Add `groupingUsed: bool = False` and `groupCount: int = 0` to `ReviewMetrics`.

### 6. Multi-stage engine

`reasoning/multi_stage.py` is legacy/debug — do **not** wire grouping there. Note this in `docs/architecture/reasoning-engine.md`.

## Tests (`tests/test_grouper.py`, new; extend engine tests)

1. `test_reconcile_assigns_every_file_once` — gaps and duplicates fixed; solo groups appended.
2. `test_reconcile_drops_out_of_range_indices`.
3. `test_reconcile_splits_oversized_groups` — 23 files, max 10 → 10+10+3.
4. `test_split_by_budget` — group over byte budget splits; per-file diff sizes mocked.
5. `test_grouping_never_merges_across_groups` — two small groups stay two chunks even though combined < max_bytes.
6. `test_unparseable_llm_response_falls_back` — result identical to `build_chunks`, `groups.json` records `bytes-fallback` + reason.
7. `test_bytes_mode_skips_llm` — no Pi call made.
8. `test_auto_mode_threshold` — 3 files → no grouping call; 4 files → call.
9. `test_label_in_chunk_prompt` — engine prompt for a labeled chunk contains the group label (assert on the prompt text passed to the mocked runner).
10. `test_groups_artifact_written`.
11. `test_single_oversized_file_still_truncated` — existing truncation semantics preserved inside grouping path.

Mock `PiRunner` and `run_git` per existing test patterns.

## Docs

- `docs/architecture/pipeline.md` + `docs/architecture/reasoning-engine.md`: grouping step, guards, fallback guarantee.
- `docs/reference/artifacts.md`: `groups.json`.
- `docs/reference/schemas.md`: `FileGroup`/`GroupingResult`.
- `docs/reference/environment-variables.md`: three new variables.
- `CHANGELOG.md`.

## Verification

```bash
python -m pytest tests/test_grouper.py -v
python -m pytest
GROUPING_MODE=llm reviewforge review --dry-run   # large PR; inspect groups.json
```

## Acceptance criteria

1. Related files (change + its tests, interface + implementation) land in one chunk whenever the grouping model identifies them.
2. Any grouping failure produces byte-identical behavior to the current chunker.
3. No chunk ever mixes files from different groups; no group exceeds `grouping_max_files` or the byte budget.
4. `groups.json` always records which mode ran and why.
5. With `GROUPING_MODE=bytes`, zero behavioral change (no LLM call, same chunks).
