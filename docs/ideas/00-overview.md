# ReviewForge × OpenCodeReview — Implementation Plan Index

Six implementation plans, each a self-contained agent prompt. Every plan assumes the agent works in the `moeller-projects/reviewforge` repository on a fresh feature branch and has read `AGENTS.md` first.

## Execution order (recommended)

| Order | Plan | Branch | Size | Depends on |
|---|---|---|---|---|
| 1 | `05-comment-args-repair.md` | `feat/json-salvage-repair` | S | — |
| 2 | `01-review-filter-stage.md` | `feat/review-filter-stage` | M | — |
| 3 | `04-run-transcript-jsonl.md` | `feat/run-transcript` | M | — |
| 4 | `02-anchor-resolver.md` | `feat/snippet-anchor-resolver` | L | — |
| 5 | `06-per-language-rules.md` | `feat/per-language-rules` | M | — |
| 6 | `03-semantic-grouping.md` | `feat/semantic-grouping` | L | 02 (snippet anchors make group-merge safer) |

Plans 1–5 are mutually independent and can run in parallel on separate branches. Plan 6 touches the same engine files as 2 and 3 — run it last or rebase carefully.

## Shared conventions (apply to every plan)

1. **Read `AGENTS.md` before writing code.** Implementation under `src/reviewforge/` is authoritative; do not infer behavior from older docs or OpenSpec history.
2. **Pipeline stages** live in `src/reviewforge/pipeline/stages/`, one `Stage` subclass per module, registered in `src/reviewforge/pipeline/stages/__init__.py` in `DEFAULT_PIPELINE`, `REVIEW_ONLY_PIPELINE`, `FAST_REVIEW_PIPELINE`, and `FAST_REVIEW_REVIEW_ONLY_PIPELINE` (never `POST_ONLY_PIPELINE` unless stated).
3. **Config** flows through `src/reviewforge/config.py` (`Config` dataclass + `_ENV_ALIASES`). Every new knob needs: dataclass field with default, env alias, entry in `docs/reference/environment-variables.md`.
4. **Schemas** live in `src/reviewforge/pipeline/schemas.py`. Pydantic v2, `extra="ignore"`, no coercion. New model outputs get new schema classes + validation via `validate_payload()`.
5. **Artifacts** are written via `artifacts.builder.write_json` and documented in `docs/reference/artifacts.md`.
6. **Fail-open for optimizations.** Anything that reduces noise (filter, grouping, repair) must degrade to current behavior on error — never fail the run, never silently drop findings outside the designed path.
7. **Tests** mirror existing layout under `tests/` (`test_stages.py`, `test_schemas.py`, …). Run `python -m pytest` before finishing. No network, no real Pi invocations in unit tests — mock `PiRunner`.
8. **OpenSpec**: after implementation, scaffold `openspec/changes/<change-name>/{proposal.md,design.md,tasks.md}` summarizing the change, per repo convention.
9. **CHANGELOG.md**: add entry under `## Unreleased`.
10. **Attribution**: algorithms ported from `alibaba/open-code-review` carry a header comment: `# Ported from alibaba/open-code-review (Apache-2.0), <source file>.` Add the project to `LICENSE` notices / `NOTICE` if the repo has one.

## Source material

All referenced OpenCodeReview (OCR) sources: `github.com/alibaba/open-code-review`, Apache-2.0. Key files cited per plan:

- `internal/config/template/prompts/review_filter_task_system.md` → Plan 01
- `internal/diff/resolver.go`, `internal/diff/relocation.go`, `internal/config/template/prompts/re_location_task_system.md` → Plan 02
- `internal/agent/grouping.go`, `internal/config/template/prompts/grouping_task_system.md` → Plan 03
- `internal/session/`, `internal/viewer/`, viewer docs → Plan 04
- `internal/tool/comment_args_repair.go` → Plan 05
- `internal/config/rules/`, `internal/agent/selection.go` → Plan 06
