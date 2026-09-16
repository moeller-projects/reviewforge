# Plan 04 — Structured Run Transcript (JSONL Event Log)

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/run-transcript` · **Size:** M

## Context

A review run currently produces: per-run artifacts (`run-summary.json`, `final-findings.json`, `review-result.json`, diff, …, see `docs/reference/artifacts.md`), token usage in `ReviewMetrics`, and human-readable logs via `reviewforge/runlog.py`. What does **not** exist: a machine-readable, ordered record of *what happened during the run* — which LLM calls were made, what they cost, which files were deliberately reviewed vs. silently errored, why each finding was dropped.

OpenCodeReview writes an append-only JSONL transcript per session (`internal/session/`, rendered by `internal/viewer/`): one event per line — `llm_request`, `llm_response`, `tool_call`, `review_item_done`, `review_item_failed`. Two lessons to carry over:

1. **"Failure dressed up as silence"**: a file with zero findings is a clean review only if the model deliberately finished; an errored item looks identical in the final output. Distinguish `review_item_done` from `review_item_failed` at the source.
2. **Append-only JSONL survives crashes**: a killed run still yields a valid partial transcript. Pretty-printed single-JSON summaries cannot do this.

This transcript is the ingest contract for the planned reviewforge backend (`POST /api/reviews` will carry `ReviewResult` + transcript), so design it as a versioned public format, not a debug dump.

## Goal

Every run writes `run-transcript.jsonl` — one JSON event per line, ordered, versioned — covering stage lifecycle, LLM request/response (with token usage), per-file/chunk review outcomes, and per-finding keep/drop decisions with fingerprints.

## Non-goals

- No viewer/UI (the backend will consume this; OCR's viewer is reference only).
- No full prompt/response bodies in the default mode (size + secrecy; pointers + byte counts instead — see `transcript_include_content`).
- No changes to existing artifacts — this is additive.

## Design

```text
orchestrator ──► TranscriptWriter (artifacts/run-transcript.jsonl, append-only)
                      ▲        ▲           ▲              ▲
                 stage events  PiRunner   engines      validate_anchors /
                             (llm_*)   (review_item_*)  filter (finding_*)
```

Event envelope (every line):

```json
{"v": 1, "type": "<event_type>", "ts": "<ISO-8601 UTC>", "run_id": "<cfg.review_run_id>", "pr_id": 123, ...}
```

## Event types (v1)

| `type` | Emitted by | Extra fields |
|---|---|---|
| `run_start` | orchestrator | `engine`, `mode` (review/review_only/post_only), `config_digest` (non-secret config: model, flags — **never** tokens) |
| `stage_start` / `stage_end` | orchestrator | `stage`, `duration_ms` (end), `status` (`ok`/`failed`/`skipped`), `error`? |
| `llm_request` | `PiRunner` | `purpose` (free-form: `review`, `review_chunk[2]`, `filter`, `grouping`, `repair`, `anchor_relocate`), `session_id`, `prompt_bytes`, `prompt_artifact`? (pointer if persisted) |
| `llm_response` | `PiRunner` | `purpose`, `duration_ms`, `usage: {in, out, total}`, `ok`, `error`? |
| `review_item_done` | engines | `item` (file path or chunk/group label), `finding_count`, `deliberate: true` — model finished this item cleanly |
| `review_item_failed` | engines | `item`, `error` — item produced nothing **and this is not a clean review** |
| `finding_recorded` | engines | `title`, `file`, `line`, `severity`, `fingerprint` (v2 key from `ado/posting.py`) |
| `finding_dropped` | validate_anchors, filter (Plan 01) | `title`, `file`, `line`, `fingerprint`, `category` (`anchor`/`filter`/`rerun_filter`), `reason` |
| `finding_posted` | post_to_ado | `fingerprint`, `thread_id` |
| `vote_cast` | post_to_ado | `vote` (e.g. `-5`), `reason` |
| `run_end` | orchestrator | `verdict` (`ok`/`failed`), `findings_posted`, `findings_dropped`, `duration_ms`, `tokens: {in, out, total}` |

## Implementation steps

### 1. Writer module (`src/reviewforge/runlog/transcript.py`, new file)

```python
"""Append-only JSONL run transcript.

Event taxonomy inspired by alibaba/open-code-review (Apache-2.0),
internal/session/. Lines are flushed after every write so a killed run
leaves a valid partial transcript.
"""
from __future__ import annotations

import json, os, threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

class TranscriptWriter:
    def __init__(self, path: Path, *, run_id: str, pr_id: int | str,
                 include_content: bool = False):
        self._path = path
        self._base = {"v": 1, "run_id": run_id, "pr_id": pr_id}
        self._include_content = include_content
        self._lock = threading.Lock()
        path.parent.mkdir(parents=True, exist_ok=True)

    def emit(self, type_: str, **fields: Any) -> None:
        event = {**self._base, "type": type_,
                 "ts": datetime.now(timezone.utc).isoformat(), **fields}
        line = json.dumps(event, ensure_ascii=False, default=str)
        with self._lock, self._path.open("a", encoding="utf-8") as fh:
            fh.write(line + "\n")
            fh.flush()
            os.fsync(fh.fileno())   # crash-safety over speed; one fsync per event is fine at this volume


class NullTranscript:
    """No-op stand-in so call sites never branch on None."""
    def emit(self, type_: str, **fields: Any) -> None: ...
```

### 2. Wiring

- **Orchestrator** (`src/reviewforge/pipeline/orchestrator.py`): construct `TranscriptWriter(ctx.artifacts.dir / "run-transcript.jsonl", ...)` (check `artifacts/manager.py` for the canonical run directory accessor) at run start; store on `StageContext` as `ctx.transcript` (add the field in `pipeline/context.py`, typed `TranscriptWriter | NullTranscript`). Emit `run_start`, `stage_start`/`stage_end` (wrap the existing per-stage timing in `run_stages()`), `run_end` in the finalization path that writes `run-summary.json`.
- **PiRunner** (`src/reviewforge/ai/runner.py`): accept an optional transcript (constructor param or attribute, default `NullTranscript`). Emit `llm_request` before spawn (purpose passed by caller — add a `purpose: str = "review"` parameter to the public run method and thread it through engine call sites), `llm_response` after, with parsed token usage (the `_TOKEN_RE` path) and `ok`/`error`. The existing JSON-repair invocation emits `purpose="repair"`.
- **Engines** (`reasoning/single_pi.py`): after each chunk review, emit `review_item_done` (item = chunk label or file list joined) with the chunk's finding count; on chunk failure, `review_item_failed`. For each finding in the merged result, emit `finding_recorded` with fingerprint computed via the existing v2 key function in `ado/posting.py` (import the public helper; if it is private (`_`-prefixed), promote a public `finding_fingerprint()` and update posting internals to use it).
- **validate_anchors / filter stages**: emit `finding_dropped` per dropped finding (fingerprint + category + reason).
- **post_to_ado**: emit `finding_posted` (thread id from the ADO response) and `vote_cast`.
- Engines/stages must tolerate `NullTranscript` — transcript emission never raises into the pipeline. Wrap `emit` calls defensively (`try/except Exception → runlog.warning`) at the **writer** level, not every call site: make `TranscriptWriter.emit` itself non-raising.

### 3. Config (`src/reviewforge/config.py`)

| Field | Type | Default | Env alias |
|---|---|---|---|
| `transcript_enabled` | `bool` | `True` | `TRANSCRIPT_ENABLED` |
| `transcript_include_content` | `bool` | `False` | `TRANSCRIPT_INCLUDE_CONTENT` — when on, `llm_request`/`llm_response` include full prompt/response text (debug only; warn in docs about code + token content) |

### 4. `config_digest` allowlist

`run_start.config_digest` includes only: `engine`, `model` (`pi_model`), `anchor_policy`, `filter_enabled`, `grouping_mode`, `review_language`, `fail_on`, `vote_waiting_on`, `dry_run`, chunk settings. Explicitly excluded: `ado_token`, `openai_api_key`, any `*_TOKEN`/`*_KEY` — enforce by allowlist, not denylist.

## Tests (`tests/test_transcript.py`, new)

1. `test_events_are_valid_jsonl` — every line parses; envelope fields present (`v`, `type`, `ts`, `run_id`).
2. `test_event_order` — `run_start` first, `run_end` last, `stage_start` precedes its `stage_end` (full mocked run via orchestrator test harness, see `tests/test_stages.py` patterns).
3. `test_partial_transcript_on_crash` — stage raises mid-run → transcript still parses, contains events up to the failure.
4. `test_finding_events_carry_fingerprints` — `finding_recorded`/`finding_dropped` fingerprints match `ado.posting` v2 key for the same finding.
5. `test_item_done_vs_failed` — clean chunk emits `review_item_done`; failing chunk emits `review_item_failed`, never both for one item.
6. `test_no_secrets_in_transcript` — run with dummy tokens in env; assert no transcript line contains them.
7. `test_disabled_writes_nothing` — `transcript_enabled=False` → no file.
8. `test_emit_never_raises` — read-only target dir → warning logged, run continues.
9. `test_token_usage_recorded` — `llm_response.usage` matches runner-parsed values.

## Docs

- `docs/reference/artifacts.md`: full `run-transcript.jsonl` section — envelope, event table, versioning rule (`v` bumps on breaking field changes; additive fields are not breaking), crash-partial guarantee.
- `docs/architecture/artifacts.md`: where the writer sits.
- `docs/reference/environment-variables.md`: two new variables, with the `TRANSCRIPT_INCLUDE_CONTENT` secrecy warning.
- `CHANGELOG.md`.

## Verification

```bash
python -m pytest tests/test_transcript.py -v
python -m pytest
reviewforge review --dry-run && tail -n 20 <run-dir>/run-transcript.jsonl | jq .
```

## Acceptance criteria

1. Every run (review, review-only, dry-run) produces a valid `run-transcript.jsonl`; killed runs produce valid partials.
2. Every finding's lifecycle is traceable: `finding_recorded` → (`finding_dropped` | `finding_posted`), keyed by the same v2 fingerprint used in `prb:` markers.
3. A silent chunk failure is distinguishable from a clean review via `review_item_failed` vs `review_item_done`.
4. No secret appears in any event (allowlist-tested).
5. Transcript failure never fails the run.
