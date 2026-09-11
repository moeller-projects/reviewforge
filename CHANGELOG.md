# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Changed

- `single_pi` now receives the configured coding standards, and the canonical `ReviewResult` retains a `PrSummary` with `work_type` and `biggest_unknown`, plus `test_gaps`, `escalation_hints`, and `discarded_findings`. Escalation hints are recorded and, when `ESCALATION_REVIEW_ENABLED=1`, trigger a focused second pass.
- CRG internals are consolidated under `reviewforge.pipeline.crg`; shipped configuration, artifact, marker, and stage contracts are unchanged.

### Added

- Reasoning engine abstraction with `single_pi` as the production default and `multi_stage` as an explicit legacy fallback.
- Canonical `ReviewResult` response schema, projection layer, run metadata, and richer evidence/metrics artifacts.
- `REASONING_ENGINE` configuration and `FAST_REVIEW` compatibility alias.
- **Single Pi reasoning engine** (`REASONING_ENGINE=single_pi`). Runs the same Pi-driven work in one model call. Alias: `FAST_REVIEW=1` / `--fast-review`. See `docs/architecture/pipeline.md` and `docs/reference/configuration.md` for details.
- New prompt file `prompts/fast-review-system.md` used by the `single_pi` engine.
- New Pydantic schemas in `reviewforge.pipeline.schemas`: `ReviewResult`, `PrSummary`, `RichFinding`, `RichEvidence`, `ReviewMetrics`, `ReviewConfidence`, plus the legacy `FastReviewResult`, `ContextSummary`, `ReviewSummary`, `VerificationSummary`, `ReviewStatistics`.
- Optional CRG wave-two context: deterministic API-surface diffing, critical-flow tracing, and hub/bridge architecture grounding. All are independently flag-gated and default off pending shadow benchmarking.
- New artifact `review-result.json` (appended to `ARTIFACT_NAMES`).
- Dedupe markers now use rewording-stable v2 keys and recognize existing v1 markers during the one-time dual-key transition.
- Added the narrow `ModelRunner` seam with `PiCliRunner`; `PiRunner` remains a deprecated compatibility alias for one release.
- PowerShell operations wrappers are deprecated compatibility entrypoints; use `python -m reviewforge.ops` for cross-platform container build and review commands.
- ADO pipeline fetch and post operations now execute in-process through `ado.operations`; `reviewforge.ado.cli` remains a legacy compatibility shim.
- Follow-up reviews now curate deterministic human feedback from prior bot threads and suppress dismissed re-raises while preserving explicit regressions.
- Optional CRG context enrichment (`CRG_ENABLED=1`): a `code-review-graph` Tree-sitter stage between repository preparation and reasoning injects deterministic change-impact analysis (changed functions, blast radius, test gaps, risk scores) into the single-pi prompt and persists it as `crg-analysis.json`. New knobs: `CRG_CACHE_DIR` (persistent graph-cache root, container default `/workspace/crg-cache` on the `reviewforge-crg-cache` volume) and `CRG_CONTEXT_MAX_BYTES` (prompt block cap, default 8192).

### Fixed

- CRG graph cache now persists across runs: the cold/warm decision is made before the graph store eagerly creates the SQLite file, warm updates receive the PR's changed files instead of an unreliable `HEAD~1` guess, the cache path is keyed by the pinned CRG tool version, and the container image actually ships `code-review-graph` (locked in `uv.lock`, installed via `uv export --extra crg`) with a dedicated `reviewforge-crg-cache` volume mounted at `/workspace/crg-cache`.
- CRG base snapshots are now keyed by the CRG tool version, so a tool upgrade cannot reuse an incompatible snapshot.
- CRG failures now write `crg-analysis.json` with `status: "failed"`, and truncated analyses surface as `status: "degraded"`.
- Fixed wave-two API candidates to require surviving source `CALLS` edges, corrected the package flow API usage and entry classification, isolated base snapshots in disposable worktrees/databases, and added exact base-SHA cache reuse evidence.
- Fixed progressive disclosure staging to expose full `changed-files.json`, register disposable staging paths for cleanup, and keep staged preambles/pointers in chunk 1 only; added skip-path and redaction regression coverage.
- Chunked `single_pi` reviews now make one in-session synthesis call for model-written whole-PR summaries while preserving programmatic finding/uncertainty merging. If synthesis fails validation or execution, deterministic boilerplate remains a safe fallback and is recorded as `synthesisFallback`.
- Shallow merge-base resolution now checks repository state before escalating to guarded `--unshallow` fetches and reports every attempted depth when no merge base exists.
- Added tracked-path convention checks, focused boundary/error coverage, and raised the CI coverage gate to 97%.
- Scheduled PowerShell tasks now start in the repository root and resolve `uv` or a synced repository `.venv` deterministically instead of falling back to bare `python`.

### Security

- Scheduled task registration no longer embeds `-AdoToken` in Task Scheduler arguments or XML. Scheduled runs load `ADO_AUTH_TOKEN` or `ADO_API_KEY` from the `.env` file referenced by `-EnvFile`.

### Deprecated

- `reviewforge.ado.cli` is scheduled for removal in 0.4.0; the compatibility shim and its `sys.modules` self-replacement will be removed together. Use `reviewforge.ado.operations` or the primary `reviewforge` CLI.

### Removed

- `intent.json`, `context-plan.json`, `collected-context.json`, `context-digest.json`, `candidate-findings.json`, `verified-findings.json`, and `severity-findings.json`. Consumers must read canonical `review-result.json` or the `final-findings.json` projection.
