# Plan 06 — Per-Language Review Rules with Deterministic Matching

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/per-language-rules` · **Size:** M

## Context

Review standards today are global: one `REVIEW_STANDARDS_PATH` (default `standards/clean-code.md`) applies to every file in every PR. A Python-specific rule (e.g. "mutable default arguments") and a Terraform-specific rule (e.g. "state file handling") share one document, so the model carries irrelevant rules for every file it reviews — attention noise, and the standards file grows into a least-common-denominator document.

OpenCodeReview ships 52 language-specific rule docs (`internal/config/rules/rule_docs/*.md`) and matches them to files deterministically (extension/path-based, `internal/agent/selection.go` + rules resolution). Their claim, validated at scale: *"template-engine-based rule matching keeps the model's attention sharply focused and eliminates information noise at the source — more stable and predictable than purely language-driven rule guidance."*

Their delegation mode adds a second useful idea: **group files by rule content** — files sharing the same rule set appear under one heading, so identical rules are never repeated in the prompt.

## Goal

Add a `rules/` directory of per-language rule documents, deterministic file→rule resolution, and prompt injection grouped by rule set. Global standards remain the baseline; language rules are additive and focused.

## Non-goals

- No per-file rule *authoring* by the model — matching is 100% deterministic.
- No user-defined custom rule DSL (OCR has path-filtered custom rules; keep v1 to shipped rules + one repo-local override directory).
- No removal of `REVIEW_STANDARDS_PATH` — it stays and applies globally.

## Design

```text
changed files ──► resolve_rules() ──► {rule_set_id: [files]}
                        │                    │
                        │                    ├─ prompt section per chunk/group:
                        │                    │   "Applicable review rules"
                        │                    │   (each rule set once, with its file list)
                        │                    └─ rules-report.json artifact
                        └─ precedence: repo override (.reviewforge/rules/) > shipped rules/ > default.md
```

## Implementation steps

### 1. Rule documents (`rules/`, new top-level directory)

Start with eight — write them fresh, informed by OCR's `rule_docs/` (Apache-2.0; if you port text wholesale, add the attribution header per file):

| File | Covers |
|---|---|
| `rules/default.md` | Fallback for any unmatched file — short, generic checklist |
| `rules/python.md` | Mutable defaults, exception swallowing, `__all__`, typing, async pitfalls |
| `rules/powershell.md` | `$ErrorActionPreference`, pipeline semantics, quoting/injection, `ShouldProcess` |
| `rules/ts_js.md` | `==`/`===`, promise error handling, prototype pollution, strict null |
| `rules/go.md` | goroutine leaks, `defer` in loops, error wrapping, map concurrency |
| `rules/yaml.md` | Anchors, implicit typing (`yes`/`no` booleans), indentation |
| `rules/terraform.md` | State handling, `count` vs `for_each`, sensitive outputs |
| `rules/dockerfile.md` | Layer caching, secrets in layers, non-root, pinned versions |

Each file: 15–40 focused, checkable rules, markdown bullets, no prose essays. Keep them short — they are prompt tokens spent per review.

### 2. Manifest (`rules/manifest.json`)

```json
{
  "version": 1,
  "rules": [
    {"id": "python",     "file": "python.md",     "extensions": [".py", ".pyi"]},
    {"id": "powershell", "file": "powershell.md", "extensions": [".ps1", ".psm1", ".psd1"]},
    {"id": "ts_js",      "file": "ts_js.md",      "extensions": [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts"]},
    {"id": "go",         "file": "go.md",         "extensions": [".go"]},
    {"id": "yaml",       "file": "yaml.md",       "extensions": [".yml", ".yaml"]},
    {"id": "terraform",  "file": "terraform.md",  "extensions": [".tf", ".tfvars"]},
    {"id": "dockerfile", "file": "dockerfile.md", "globs": ["Dockerfile", "Dockerfile.*", "*.dockerfile"]}
  ],
  "default": "default.md"
}
```

### 3. Resolver module (`src/reviewforge/ai/rules.py`, new file)

```python
"""Deterministic file → review-rule resolution.

Concept ported from alibaba/open-code-review (Apache-2.0),
internal/config/rules/. Matching is pure: extension map, then globs,
then default. The model never chooses rules.
"""
from __future__ import annotations

import fnmatch, json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class RuleSet:
    id: str
    path: Path
    content: str


def load_manifest(rules_dir: Path) -> dict: ...


def resolve_rules(files: list[str], rules_dir: Path,
                  override_dir: Path | None = None) -> dict[str, "RuleMatch"]:
    """Group files by applicable rule set.

    Precedence per file:
      1. override_dir/<same manifest layout> — repo-local rules win entirely
         for files they match (no merging; a repo owns its overrides)
      2. shipped manifest: extension match → glob match
      3. default.md

    Returns {rule_id: RuleMatch(rule_set, files)} — grouped BY RULE SET,
    so each rule document appears once in the prompt with its file list
    (OCR delegate-mode insight: never repeat identical rule text).
    Missing rule files referenced by the manifest → warning + default.
    """


def render_rules_section(matches: dict[str, "RuleMatch"], max_bytes: int) -> str:
    """Render the prompt section:

    ## Applicable review rules
    ### Rules: python (applies to: src/a.py, src/b.py)
    <content>
    ### Rules: default (applies to: README.md)
    <content>

    Byte-capped per rule set; on cap, truncate with a marker (rules are
    checklists — a truncated tail loses rules, never corrupts them).
    """
```

### 4. Prompt injection (`src/reviewforge/reasoning/single_pi.py`)

- In `_build_single_pi_prefix` (non-chunked path): after the standards section, append `render_rules_section(resolve_rules(changed_files, ...))`.
- In the chunk path: resolve rules **per chunk's file list** so each chunk carries only its own languages' rules (this is where the attention-focusing pays off most).
- Standards vs rules ordering in the prompt: global standards first (baseline), language rules second (specific). Add one instruction line: *"Language-specific rules below extend the global standards; where they conflict, the language rule wins for files in its list."*
- The review system's JSON contract does not change.

### 5. Config (`src/reviewforge/config.py`)

| Field | Type | Default | Env alias |
|---|---|---|---|
| `rules_enabled` | `bool` | `True` | `REVIEW_RULES_ENABLED` |
| `rules_path` | `str` | `"rules"` | `REVIEW_RULES_PATH` (shipped dir; container image copies it to `/app/rules` — update `Dockerfile` + `run.ps1` env defaults alongside `REVIEW_STANDARDS_PATH` handling) |
| `rules_override_path` | `str` | `".reviewforge/rules"` | `REVIEW_RULES_OVERRIDE_PATH` (repo-local; missing dir = no overrides, not an error) |
| `rules_max_bytes` | `int` | `20000` | `REVIEW_RULES_MAX_BYTES` (per rule set, see render cap) |

### 6. Artifact

`rules-report.json` per run: `{"matches": [{"rule_id", "files": [...], "source": "shipped"|"override"|"default"}], "unmatched_using_default": [...]}`. Write via `write_json` from the engine after resolution. Document in `docs/reference/artifacts.md`.

### 7. Container/packaging

- `Dockerfile`: `COPY rules/ /app/rules/` next to the existing standards/prompts copies.
- `run.ps1` / `ops.py` container env wiring: pass `REVIEW_RULES_PATH=/app/rules` unless overridden — mirror exactly how `REVIEW_STANDARDS_PATH` is propagated today (check `run.ps1` and `reviewforge/ops.py`).
- `pyproject.toml`: if the package ships data files, add `rules/` to package data; otherwise document that rules are read from the repo/image path, not the wheel.

## Tests (`tests/test_rules.py`, new)

1. `test_extension_mapping` — `.py` → python, `.ps1` → powershell, `.tsx` → ts_js.
2. `test_glob_mapping` — `Dockerfile` and `Dockerfile.dev` → dockerfile.
3. `test_default_fallback` — `.md`, unknown ext → default.
4. `test_grouped_by_rule_set` — 3 python files appear once under `python`, rule text rendered once.
5. `test_override_wins` — override dir with custom `python.md` → its content used, source `override`.
6. `test_override_missing_dir_ok` — no `.reviewforge/rules` → no error, source `shipped`.
7. `test_missing_rule_file_falls_back` — manifest references absent file → warning + default.
8. `test_render_byte_cap` — oversized rule doc truncated with marker.
9. `test_prompt_contains_rules_section` — engine prefix (mocked runner) contains rendered section and the conflict-precedence line.
10. `test_chunk_scoped_rules` — chunk with only `.go` files gets go rules, not python rules.
11. `test_disabled` — `rules_enabled=False` → no section, no artifact, byte-identical prompt to before.
12. `test_rules_report_written`.

## Docs

- `docs/reference/prompts.md`: rules section, ordering, conflict rule.
- `docs/reference/configuration.md` + `environment-variables.md`: four new knobs.
- `docs/guides/prompt-development.md`: how to add a language (manifest entry + rule doc + test).
- `docs/architecture/ai.md`: deterministic rule matching in the context-assembly description.
- `CHANGELOG.md`.

## Verification

```bash
python -m pytest tests/test_rules.py -v
python -m pytest
reviewforge review --dry-run   # inspect rules-report.json + prompt artifact
```

## Acceptance criteria

1. Each chunk's prompt contains only the rule sets matching its files, each rendered once, grouped with its file list.
2. Repo-local overrides win wholesale; their absence is not an error.
3. Matching is deterministic — identical file lists always produce identical sections (diff-able in tests).
4. `REVIEW_RULES_ENABLED=0` restores byte-identical prompts.
5. Container image ships `/app/rules` and the runner env wiring matches the standards-path pattern.
