# Semantic Diffing Plan

## Goal

Add semantic diffing to the PR reviewer so review stages receive more than raw unified diff text. The reviewer should understand changes at symbol/function/class level and persist that understanding as review artifacts.

Semantic diffing should improve:

- intent reconstruction,
- context planning,
- finding precision,
- verification quality,
- auditability of why a review reached its conclusions.

## Non-goals

- Do not replace unified diffs. Semantic diff is additional context.
- Do not introduce heavy clean-architecture indirection.
- Do not block reviews when semantic parsing fails.
- Do not attempt full multi-language precision in the first iteration.

## Recommended first scope

Start with:

- Python
- JavaScript
- TypeScript

Fallback behavior:

- if parsing fails,
- language is unsupported,
- or parser dependencies are unavailable,

then emit best-effort file-level metadata and continue the review.

## Proposed artifact outputs

Each review run should write:

```text
artifacts/pr-<id>/runs/<run-id>/semantic-diff.json
artifacts/pr-<id>/runs/<run-id>/semantic-summary.md
```

### `semantic-diff.json` draft schema

```json
{
  "enabled": true,
  "parser": "tree-sitter|fallback",
  "files": [
    {
      "path": "src/example.py",
      "language": "Python",
      "status": "modified",
      "symbols": [
        {
          "name": "calculate_total",
          "kind": "function",
          "changeType": "modified",
          "oldSignature": "calculate_total(items)",
          "newSignature": "calculate_total(items, discounts=None)",
          "oldRange": { "startLine": 10, "endLine": 31 },
          "newRange": { "startLine": 10, "endLine": 44 },
          "riskNotes": [
            "signature changed",
            "function body grew significantly"
          ]
        }
      ],
      "fallbackNotes": []
    }
  ],
  "summary": {
    "addedSymbols": 1,
    "removedSymbols": 0,
    "modifiedSymbols": 3,
    "signatureChanges": 1,
    "unsupportedFiles": []
  }
}
```

### `semantic-summary.md` draft shape

```markdown
# Semantic diff summary

## src/example.py

- Modified function `calculate_total(items)` → `calculate_total(items, discounts=None)`
  - Lines: 10-31 → 10-44
  - Risk notes:
    - signature changed
    - function body grew significantly

## Summary

- Added symbols: 1
- Removed symbols: 0
- Modified symbols: 3
- Signature changes: 1
```

## Target module placement

```text
scripts/
  infrastructure/
    git/
      semantic_diff.py
  infrastructure/
    artifacts/
      builder.py
  pipeline/
    orchestrator.py
```

Rationale:

- semantic diffing is fundamentally derived from git revisions and changed files,
- artifacts builder can serialize summaries,
- orchestrator wires the result into stages.

## Configuration

Add `.env` flags:

```dotenv
ENABLE_SEMANTIC_DIFF=1
SEMANTIC_DIFF_MAX_BYTES=50000
```

Suggested behavior:

- default enabled once stable,
- initially default disabled if parser dependency is uncertain,
- truncate semantic summary before injecting into prompts using `SEMANTIC_DIFF_MAX_BYTES`.

## Task breakdown

### T-01 — Define semantic diff output schema

Estimate: S

Depends on: none

Done when:

- `semantic-diff.json` schema is documented,
- symbol change types are defined,
- fallback behavior is specified.

Change types:

```text
added
removed
modified
signature-changed
unknown
```

### T-02 — Add parser backend

Estimate: M

Depends on: T-01

Done when:

- Python module can parse changed files into symbols,
- Python functions/classes are supported,
- JS/TS functions/classes/methods are supported,
- unsupported languages return fallback metadata.

Recommendation:

- prefer Tree-sitter if dependency cost is acceptable,
- otherwise start with AST for Python and lightweight regex for JS/TS.

### T-03 — Add `infrastructure/git/semantic_diff.py`

Estimate: M

Depends on: T-02

Done when:

- module reads base and source versions of changed files,
- extracts symbols from both revisions,
- returns structured semantic diff data.

Responsibilities:

- call `git show <commit>:<path>`,
- detect language,
- parse old/new symbols,
- compare symbol maps,
- emit added/removed/modified/signature-changed results.

### T-04 — Compare base vs source symbols

Estimate: L

Depends on: T-03

Done when:

- added symbols are detected,
- removed symbols are detected,
- modified symbols are detected,
- signature changes are detected,
- moved/renamed candidates are noted best-effort.

Comparison strategy:

1. match by stable key: `kind + name`,
2. compare signature,
3. compare normalized body hash,
4. if removed and added bodies are similar, mark possible rename/move.

### T-05 — Wire semantic diff into artifacts

Estimate: S

Depends on: T-04

Done when:

- each run writes `semantic-diff.json`,
- each run writes `semantic-summary.md`,
- artifacts are included in run-scoped artifact directory.

### T-06 — Feed semantic diff into review stages

Estimate: M

Depends on: T-05

Done when:

- intent stage receives semantic summary,
- context-plan stage receives semantic summary,
- findings stage receives semantic summary,
- verify/severity stages receive semantic summary indirectly via artifacts/context.

Implementation notes:

- append semantic summary to stage instructions,
- never replace unified diff,
- truncate by `SEMANTIC_DIFF_MAX_BYTES`,
- include a note when semantic diff is partial/fallback.

### T-07 — Add config flags

Estimate: S

Depends on: T-06

Done when:

- `Config` includes semantic diff fields,
- `.env.example` documents the flags,
- README documents the behavior.

Flags:

```dotenv
ENABLE_SEMANTIC_DIFF=1
SEMANTIC_DIFF_MAX_BYTES=50000
```

### T-08 — Add tests

Estimate: M

Depends on: T-04

Done when:

- pytest covers Python added/removed/modified functions,
- pytest covers JS/TS added/removed/modified functions,
- fallback behavior is tested,
- prompt injection truncation is tested.

Suggested fixtures:

```text
tests/fixtures/semantic/python_before.py
tests/fixtures/semantic/python_after.py
tests/fixtures/semantic/typescript_before.ts
tests/fixtures/semantic/typescript_after.ts
```

## Critical path

```text
T-01 → T-02 → T-03 → T-04 → T-05 → T-06 → T-08
```

## Risks

- Multi-language parsing can grow quickly.
- Tree-sitter may add dependency/build complexity.
- Regex fallback can produce false confidence if not clearly marked.
- Large semantic summaries can bloat prompt input.
- Renames/moves are difficult to detect accurately without more expensive similarity checks.

## Mitigations

- Start with limited language support.
- Always mark parser mode: `tree-sitter`, `ast`, `regex`, or `fallback`.
- Never fail the review only because semantic diff fails.
- Truncate semantic summary for prompt injection.
- Persist full semantic diff artifact for debugging.

## Recommended rollout

1. Add artifact generation only.
2. Validate artifact quality on real PRs.
3. Add prompt injection behind `ENABLE_SEMANTIC_DIFF=1`.
4. Make semantic diff default-on only after false-positive rate improves.
