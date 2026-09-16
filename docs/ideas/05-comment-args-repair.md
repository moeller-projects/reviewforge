# Plan 05 — Deterministic JSON Salvage Repair for Model Output

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/json-salvage-repair` · **Size:** S

## Context

Engines parse Pi's JSON output strictly: `json.loads` → `validate_payload()` (`src/reviewforge/pipeline/schemas.py`). When the model emits structurally invalid JSON — most commonly prose containing unescaped double quotes inside a string, or a backslash that opens no valid escape — the parse fails. `PiRunner` (`src/reviewforge/ai/runner.py`) then pays for a **model-based repair call** ("return only JSON"), tracked by `metrics.repairInvocationCount`. If that also fails, the run fails and **every finding is lost**.

OpenCodeReview inserts a **deterministic salvage pass** before giving up (source: `internal/tool/comment_args_repair.go`, Apache-2.0): a byte-scan that escapes exactly the characters that break JSON — bare quotes in prose, control characters, illegal backslashes — then re-parses, with acceptance checks so a botched repair can never pass off prose as structure. Zero LLM cost, ~150 lines, pure function.

Their load-bearing observations to preserve:

- A bare quote inside a JSON string is either the string's terminator or content the model forgot to escape; **what follows tells the two apart** — a real terminator is always followed by `,`, `}`, `]`, `:`, or end-of-text.
- The scan's only possible error is ending a string early — so **acceptance checks** after repair are mandatory, not optional.
- Byte-scanning is UTF-8 safe (continuation bytes are ≥ 0x80).

## Goal

Add `reviewforge/ai/json_repair.py` with a pure `repair_json_text()` salvage function and wire it into every engine JSON-parse failure path **before** the model-based repair call. Expected effect: fewer paid repair invocations, fewer lost runs.

## Non-goals

- No changes to schema strictness — salvage only fixes *serialization*, never coerces *content* (a finding with unknown severity still fails validation, by design).
- No salvage of truncated output (unbalanced braces from a cut-off response) — that genuinely needs the model repair call.
- No changes to `strip_json_fences` (fence removal stays a separate pre-step).

## Implementation steps

### 1. Repair module (`src/reviewforge/ai/json_repair.py`, new file)

Port of OCR `comment_args_repair.go` (Apache-2.0 — attribution header). Full algorithm:

```python
"""Deterministic salvage for model-serialized JSON.

Ported from alibaba/open-code-review (Apache-2.0),
internal/tool/comment_args_repair.go.

The model occasionally drops one level of JSON escaping: prose quotes,
bare control characters, or backslashes that open no valid escape make
the payload unparseable, and every finding in it would be lost. This
pass escapes exactly those bytes and re-parses. Acceptance checks then
decide whether the repair is trustworthy — a scan error can only end a
string early, and the checks are built to catch that.
"""
from __future__ import annotations

import json
import re
from typing import Any

_STRUCTURAL = set(",}]:")


def _is_legal_escape(s: str, i: int) -> bool:
    """s[i-1] == '\\'; report whether s[i] opens a valid JSON escape."""
    if i >= len(s):
        return False
    c = s[i]
    if c in '"\\/bfnrt':
        return True
    if c == "u":
        return i + 5 <= len(s) and all(ch in "0123456789abcdefABCDEF" for ch in s[i + 1 : i + 5])
    return False


def _next_significant(s: str, i: int) -> str | None:
    while i < len(s):
        if s[i] not in " \t\r\n":
            return s[i]
        i += 1
    return None


def repair_json_text(s: str) -> tuple[str, int]:
    """Escape invalid bytes inside JSON strings. Returns (text, n_escaped).

    n_escaped == 0 means nothing was repaired — the caller must treat the
    original parse error as final rather than re-parsing identical text.

    Quote rule: a '"' followed (after whitespace) by ',', '}', ']', ':' or
    end-of-text is a terminator; anything else is prose content and gets
    escaped. Control chars < 0x20 get their JSON escape. A backslash that
    opens no legal escape is doubled (known limitation, ported verbatim:
    `C:\\bin` decodes to a backspace after repair — JSON cannot tell those
    apart, and guessing would corrupt genuine escapes).
    """
    out: list[str] = []
    escaped = 0
    in_string = False
    i = 0
    while i < len(s):
        c = s[i]
        if not in_string:
            if c == '"':
                in_string = True
            out.append(c)
            i += 1
            continue
        if c == "\\":
            if _is_legal_escape(s, i + 1):
                out.append(c)
                out.append(s[i + 1])
                i += 2
            else:
                out.append("\\\\")
                escaped += 1
                i += 1
        elif c == '"':
            nxt = _next_significant(s, i + 1)
            if nxt is None or nxt in _STRUCTURAL:
                in_string = False
                out.append(c)
            else:
                out.append('\\"')
                escaped += 1
            i += 1
        elif ord(c) < 0x20:
            out.append({"\n": "\\n", "\r": "\\r", "\t": "\\t", "\b": "\\b", "\f": "\\f"}.get(c, f"\\u{ord(c):04x}"))
            escaped += 1
            i += 1
        else:
            out.append(c)
            i += 1
    return "".join(s for s in out), escaped


def salvage_loads(text: str, *, known_fields: frozenset[str] | None = None,
                  expect_type: type = dict) -> tuple[Any | None, str]:
    """Parse with salvage. Returns (obj, status): 'clean' | 'repaired' | 'failed'.

    1. json.loads(text)                      → 'clean'
    2. repair_json_text + json.loads         → acceptance checks → 'repaired'
    3. otherwise                             → (None, 'failed')

    Acceptance checks for 'repaired':
    - repaired text differs (n_escaped > 0)
    - parsed type == expect_type
    - expect_type is dict and known_fields given → every top-level key ∈ known_fields
      (a scan error that re-read prose as structure introduces junk keys)
    """
```

### 2. Second failure mode: array-serialized-as-string

OCR's triggering case was the model serializing an *array field* (`comments`) into a *string* containing JSON. Add to the same module:

```python
def coerce_stringified_fields(obj: dict, array_fields: frozenset[str]) -> tuple[dict, int]:
    """For each field in array_fields whose value is a str: json.loads it
    (with salvage). Returns (obj, n_coerced). Unknown/malformed → left as-is
    (schema validation will reject it, which is correct)."""
```

### 3. Wire into parse failure paths

Find every place engine output is parsed — at minimum:

- `src/reviewforge/reasoning/single_pi.py` — where the Pi output file is read and `json.loads`/`load_and_validate` runs (search for `json.loads`, `load_and_validate`, `strip_json_fences`).
- `src/reviewforge/reasoning/multi_stage.py` — same for stage outputs (`Intent`, `ContextPlan`, `ContextDigest`, `ReviewDoc`, `AcCoverageLlmResult`).
- `src/reviewforge/pipeline/schemas.py::load_and_validate` — **do not change this function's strictness**; salvage belongs at the engine call sites, not in the shared validator (posting/replay paths must stay strict).

At each engine call site, replace:

```python
raw = json.loads(path.read_text(...))
```

with:

```python
text = path.read_text(encoding="utf-8")
raw, status = salvage_loads(text, expect_type=dict)
if status == "repaired":
    log_warning(f"salvaged malformed JSON from model output ({purpose})")
    metrics_repair_salvaged = True   # thread into ReviewMetrics, see below
if raw is None:
    # existing behavior: model-based repair call / raise
```

Then, before `validate_payload`, run `coerce_stringified_fields(raw, frozenset({"findings", "good_practices", "uncertainties", "discarded_findings"}))` for `ReviewResult`-shaped payloads.

Order matters: `strip_json_fences` → `salvage_loads` → model repair call (unchanged, last resort).

### 4. Metrics

`ReviewMetrics.repairInvocationCount` exists (model-based repairs). Add `salvageRepairCount: int = Field(default=0, ge=0)` — deterministic salvages. Engines increment on `status == "repaired"`. Surface both in `run-summary.json` (already dumps metrics).

### 5. Logging

Every salvage logs one warning line: byte count escaped, purpose/engine, and whether a stringified field was coerced. Salvage is silent-success by design in output, loud in logs — you want to know the model's serialization is degrading.

## Tests (`tests/test_json_repair.py`, new)

Fixtures as inline strings — no files needed:

1. `test_clean_json_untouched` — valid JSON → `clean`, zero escapes.
2. `test_prose_quotes_escaped` — `{"observation": "the function "foo" leaks"}` → parses, content preserved.
3. `test_real_terminator_not_escaped` — quotes before `,`/`}`/`]`/`:`/end survive as terminators.
4. `test_raw_newline_in_string` — literal newline inside a string → `\n`.
5. `test_bare_backslash_doubled` — `C:\Users\dev` → parses (documents the known `C:\bin`→backspace limitation in a comment).
6. `test_legal_escapes_untouched` — `\d` invalid → doubled; `\n` valid → preserved; `\u00ff` valid → preserved.
7. `test_nothing_to_repair_returns_zero` — unparseable for structural reasons (truncated mid-object) → `failed`, caller falls through to model repair.
8. `test_acceptance_rejects_junk_keys` — repaired dict with keys outside `known_fields` → `failed`.
9. `test_stringified_findings_array_coerced` — `{"findings": "[{...}]"}` → list.
10. `test_utf8_multibyte_safe` — prose with `äöü中文` and emoji round-trips.
11. `test_review_result_end_to_end` — malformed full `ReviewResult` payload (quotes in `observation`) → salvaged → `validate_payload(ReviewResult, ...)` passes; `salvageRepairCount` incremented in the engine path (mock runner).
12. `test_severity_coercion_still_rejected` — salvaged JSON with `"severity": "critical"` still fails schema validation (salvage ≠ leniency).

## Docs

- `docs/architecture/ai.md` (or `reasoning-engine.md`): salvage layer in the output-handling description — order: fence strip → deterministic salvage → model repair → fail.
- `docs/reference/metrics.md`: `salvageRepairCount`.
- `CHANGELOG.md`.

## Verification

```bash
python -m pytest tests/test_json_repair.py -v
python -m pytest
```

## Acceptance criteria

1. The four corruption classes (prose quotes, control chars, illegal backslashes, stringified arrays) parse without a model call.
2. Structurally truncated output still routes to the model-based repair — salvage never masks real truncation.
3. Salvage never weakens schema validation (test 12).
4. `salvageRepairCount` visible in run summary; salvage events visible in logs (and in `run-transcript.jsonl` as `purpose="repair"` `llm_request` *absence* — a salvaged parse makes no LLM call — once Plan 04 lands).
