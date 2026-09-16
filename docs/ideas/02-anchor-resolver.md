# Plan 02 — Snippet-Based Anchor Resolver (3-Tier + Cross-File Re-Filing)

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/snippet-anchor-resolver` · **Size:** L

## Context

Findings are anchored today by the model's self-reported `file` + `line`. `ValidateAnchorsStage` (`src/reviewforge/pipeline/stages/validate_anchors.py`) checks `line ∈ DiffLineMapper.line_set(file)` — the line must exist in the PR diff — then drops or downgrades per `cfg.anchor_policy` (`off|drop|downgrade`). This catches *invalid* anchors but cannot fix *drifted* ones: when the model reports the wrong line (or wrong file), the finding is lost or degraded even though the model usually knows exactly which code it means.

OpenCodeReview solves position drift by making the model quote a **verbatim code snippet** (`existing_code`) and deriving line numbers deterministically (sources: `internal/diff/resolver.go`, `internal/diff/relocation.go`):

1. Match snippet against diff hunks — new side (context+added), then old side (context+deleted).
2. Fall back to scanning the full new-file content.
3. **Cross-file re-filing**: if the snippet matches a *different* changed file uniquely, move the comment there (catches declaration/implementation splits). Zero or multiple hits → decline, never guess.
4. Optional LLM re-location: regenerate the snippet, retry resolution.

Their design rule to preserve: **line numbers are derived, never trusted from the model; ambiguous matches are declined, never guessed.**

## Goal

Extend `RichFinding` with an `existingCode` snippet field and replace the line-set check in `ValidateAnchorsStage` with a 3-tier deterministic resolver + optional LLM re-location, including cross-file re-filing. Drifted-but-identifiable findings keep their correct anchor instead of being dropped.

## Non-goals

- No changes to the posting/dedupe marker format (`prb:` v2 keys still hash normalized file+line+title — resolution runs *before* posting, so keys are computed from resolved anchors).
- No re-location of work-item findings (they are file/line-less by design, see `is_work_item_finding`).
- No fuzzy/approximate string matching (Levenshtein etc.) — exact matching after whitespace normalization only. Fuzzy matching is where wrong anchors come from.

## Design

```text
for each finding (post-engine, pre-post):
  ├─ no file or work-item finding        → keep as-is (current behavior)
  ├─ has existingCode:
  │    Tier 1: snippet vs diff hunks (new side → old side)      → resolved line(s)
  │    Tier 2: snippet vs full new-file content (git show)      → resolved line(s)
  │    Tier 3: snippet vs all OTHER changed files' diffs
  │             unique hit  → re-file (file+line move together)
  │             0 or >1 hit → decline
  │    Optional Tier 4 (anchor_relocate_llm=1):
  │             LLM regenerates snippet from diff → retry Tier 1–2 once
  ├─ resolved   → update finding.line (and finding.file on re-file)
  └─ unresolved → current policy: line ∈ line_set? keep : drop/downgrade
```

## Implementation steps

### 1. Schema (`src/reviewforge/pipeline/schemas.py`)

Add to `RichFinding`:

```python
existingCode: str | None = None
```

Optional, default `None` — old artifacts and the legacy projection stay valid. No validator beyond the base (empty string is treated as absent downstream). Document in `docs/reference/schemas.md`: *"verbatim 3–8 line excerpt from the new version of the flagged code; basis for deterministic anchor resolution."*

### 2. Prompt updates

In the system prompt(s) that define the `ReviewResult` contract — `prompts/fast-review-system.md` and any single-pi prompt referenced by `FAST_REVIEW_PROMPT_PATH` — add to the finding field spec:

```markdown
- `existingCode` (string, required for every finding with a `file`): a verbatim
  copy of 3–8 consecutive lines from the NEW version of the flagged code,
  exactly as they appear in the file, including indentation. Do not paraphrase,
  do not trim indentation, do not include diff markers. This snippet is used to
  locate your finding deterministically — if it does not appear verbatim in the
  file, your finding will be dropped.
```

Note: `single_pi` builds its prompt in `reasoning/single_pi.py` referencing the system prompt file; only the prompt files change, not the Python prompt assembly.

### 3. Resolver module (`src/reviewforge/pipeline/anchors.py`, new file)

Port of OCR `resolver.go` / `relocation.go` (Apache-2.0 — attribution header). Pure functions, no I/O beyond an injected file-content reader:

```python
"""Deterministic snippet-based anchor resolution.

Ported from alibaba/open-code-review (Apache-2.0),
internal/diff/resolver.go and internal/diff/relocation.go.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable


@dataclass(frozen=True)
class ResolvedAnchor:
    file: str
    start_line: int
    end_line: int
    tier: str          # "hunk_new" | "hunk_old" | "file_content" | "cross_file" | "llm_relocated"
    refiled: bool = False  # True when tier == "cross_file" and file changed


def normalize_snippet(text: str) -> list[str]:
    """Split into lines, strip trailing whitespace, drop blank lines.

    Leading whitespace is preserved for content identity but matching
    compares lstrip-normalized forms (see _lines_match) so indentation
    drift between diff context and snippet does not break exact matches.
    """
    ...


def resolve_in_diff(snippet_lines: list[str], diff_text: str, file: str) -> ResolvedAnchor | None:
    """Tier 1: match against the file's hunks.

    Parse hunks with the existing DiffLineMapper/hunk parsing used by
    ado/diff_mapper.py (reuse it — do not write a second diff parser;
    extend it if it does not expose per-hunk old/new line sequences).
    Try new side (context + added lines, new-file numbering) first,
    then old side (context + deleted, old-file numbering).
    Consecutive normalized-line match only.
    """


def resolve_in_file_content(snippet_lines: list[str], file: str,
                            read_file: Callable[[str], str | None]) -> ResolvedAnchor | None:
    """Tier 2: scan the full new-file content line by line.

    read_file abstracts `git show <to_ref>:<path>` so tests can inject
    strings. Multiple matches → return None (ambiguous; the hunk tier
    already had its chance to disambiguate via proximity to the change).
    """


def relocate_across_files(snippet_lines: list[str], current_file: str,
                          diffs_by_file: dict[str, str]) -> ResolvedAnchor | None:
    """Tier 3: probe every OTHER changed file's diff.

    Unique hit → ResolvedAnchor(refiled=True). Zero or >1 hits → None.
    Rationale (from OCR): the agent reads related files for context and
    can describe code from a file other than the one it filed against
    (declaration/implementation split). The snippet is verbatim evidence;
    a unique match elsewhere is the finding's true home. Guessing between
    multiple matches trades one wrong location for another — decline.
    """
```

Matching helper semantics (port exactly):

```python
def _lines_match(a: str, b: str) -> bool:
    return a.strip() == b.strip()   # whitespace-insensitive, content-exact

def _match_consecutive(haystack: list[tuple[int, str]], needle: list[str]) -> tuple[int, int] | None:
    """Sliding window; haystack entries carry absolute line numbers.
    Returns (start_line, end_line) of the first full match."""
```

### 4. Optional LLM re-location (`src/reviewforge/pipeline/anchors.py` continued)

```python
RELOCATION_PROMPT = """You are a code location assistant. Given a unified diff
and a review comment, your sole task is to extract the exact code snippet from
the diff that the comment refers to. Return only a fenced code block containing
the verbatim lines, nothing else."""

def relocate_with_llm(finding, diff_text, pi_call) -> str | None:
    """Tier 4: ask the model for the snippet, extract first fenced block,
    return it or None. Caller retries Tier 1–2 with the new snippet once.
    Config-gated by anchor_relocate_llm (default off)."""
```

### 5. Stage rewrite (`src/reviewforge/pipeline/stages/validate_anchors.py`)

Keep the class name and `name = "validate_anchors"` (artifacts and docs reference it). New `run()` flow:

1. Build `diffs_by_file: dict[str, str]` — per-file diff text. The full diff is in `ctx.state.diff_text` / `ctx.artifacts.diff`; split per file using the same hunk parser from step 3 (add a `split_per_file(diff_text) -> dict[str, str]` helper to `ado/diff_mapper.py` or `pipeline/anchors.py`).
2. Build `read_file` closure: `git show {ctx.state.to_ref}:{path}` via `git.ops.run_git`, returning `None` on failure (deleted files, binary). Cache per path.
3. For each finding in `ctx.review_result.findings` (rich) — resolution operates on the **rich** findings now, not only on the projection:
   - Skip when `is_work_item_finding` equivalent (no file) — keep current pass-through.
   - If `finding.existingCode` and `cfg.anchor_snippet_enabled`: run tiers 1→2→3 (→4 if enabled). On success: set `finding.line = anchor.start_line`, `finding.file = anchor.file`, record `{"tier": anchor.tier, "refiled": anchor.refiled}` for the report.
   - If unresolved or no snippet: fall back to the **current** line-set check (`DiffLineMapper.line_set`) → keep / drop / downgrade per `cfg.anchor_policy`.
4. Re-project `ctx.final` from the updated rich findings via `pipeline.projection` (same reuse requirement as Plan 01 — find the canonical projection function and call it).
5. Artifacts:
   - Rewrite `ctx.artifacts.review_result` and `ctx.artifacts.final` (existing pattern).
   - New `anchor-report.json`: `[{"title", "file", "line", "outcome": "kept"|"resolved"|"refiled"|"downgraded"|"dropped", "tier", "from_file"?}]`.
6. Return `{"resolved": R, "refiled": F, "downgraded": D, "dropped": X}`.

### 6. Config (`src/reviewforge/config.py`)

| Field | Type | Default | Env alias |
|---|---|---|---|
| `anchor_snippet_enabled` | `bool` | `True` | `ANCHOR_SNIPPET_ENABLED` |
| `anchor_relocate_llm` | `bool` | `False` | `ANCHOR_RELOCATE_LLM` |
| `anchor_snippet_max_lines` | `int` | `12` | `ANCHOR_SNIPPET_MAX_LINES` (longer snippets are truncated before matching, log info) |

Existing `anchor_policy` stays unchanged and governs the unresolved fallback.

### 7. Metrics

Add to `ReviewMetrics`: `anchorResolved: int = 0`, `anchorRefiled: int = 0`. Increment in the stage.

## Tests (`tests/test_anchors.py`, new; extend `tests/test_stages.py`)

Unit (pure, inject strings):

1. `test_hunk_new_side_match` — snippet from added lines resolves to new-file line numbers.
2. `test_hunk_old_side_match` — snippet spanning context+deleted resolves old-side.
3. `test_whitespace_normalized_match` — snippet with different leading indentation still matches.
4. `test_blank_lines_ignored` — snippet containing blank lines matches across them.
5. `test_file_content_fallback` — snippet outside all hunks but in file → tier `file_content`.
6. `test_file_content_ambiguous_declined` — snippet appears twice in file → None.
7. `test_cross_file_unique_refile` — snippet matches exactly one other changed file → refiled, file+line updated.
8. `test_cross_file_ambiguous_declined` — matches two other files → None, finding untouched.
9. `test_no_snippet_falls_back_to_line_set` — current drop/downgrade behavior preserved.
10. `test_llm_relocation_retry` — mocked Pi returns fenced block → tier `llm_relocated`; gated off by default.

Stage-level (mock runner/git):

11. `test_drifted_line_corrected` — model line wrong, snippet right → finding kept with corrected line, not dropped.
12. `test_unresolvable_drop_records_discard` — existing `discarded_findings` behavior intact.
13. `test_anchor_report_written` — report contains per-finding outcome + tier.
14. `test_projection_regenerated` — `ctx.final` reflects corrected anchors.

Fixture diffs: build small unified-diff strings inline in the test file (3–5 hunks), no repo fixtures needed.

## Docs

- `docs/architecture/pipeline.md`: rewrite the `ValidateAnchorsStage` paragraph — snippet-first resolution, tiers, decline-on-ambiguity rule.
- `docs/reference/schemas.md`: `existingCode` field.
- `docs/reference/artifacts.md`: `anchor-report.json`.
- `docs/reference/environment-variables.md`: three new variables.
- `docs/development/prompt-development.md`: note that findings must quote `existingCode` verbatim.
- `CHANGELOG.md`.

## Verification

```bash
python -m pytest tests/test_anchors.py tests/test_stages.py -v
python -m pytest
ANCHOR_SNIPPET_ENABLED=1 reviewforge review --dry-run   # inspect anchor-report.json
```

## Acceptance criteria

1. A finding with a wrong `line` but correct `existingCode` is kept and re-anchored instead of dropped/downgraded.
2. A finding whose snippet uniquely matches another changed file is re-filed there.
3. No ambiguous match is ever guessed — ambiguous ⇒ decline ⇒ existing policy path.
4. `prb:` dedupe keys are computed from resolved anchors (resolution strictly precedes posting).
5. With `ANCHOR_SNIPPET_ENABLED=0`, behavior is byte-identical to before this change.
