# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Added

- Reasoning engine abstraction with `single_pi` as the production default and `multi_stage` as an explicit legacy fallback.
- Canonical `ReviewResult` response schema, projection layer, run metadata, and richer evidence/metrics artifacts.
- `REASONING_ENGINE` configuration and `FAST_REVIEW` compatibility alias.
- **Single Pi reasoning engine** (`REASONING_ENGINE=single_pi`). Runs the same Pi-driven work in one model call. Alias: `FAST_REVIEW=1` / `--fast-review`. See `docs/reference/pipeline.md#reasoning-engine` and `docs/reference/configuration.md` for details.
- New prompt file `prompts/fast-review-system.md` used by the `single_pi` engine.
- New Pydantic schemas in `reviewforge.pipeline.schemas`: `ReviewResult`, `PrSummary`, `RichFinding`, `RichEvidence`, `ReviewMetrics`, `ReviewConfidence`, plus the legacy `FastReviewResult`, `ContextSummary`, `ReviewSummary`, `VerificationSummary`, `ReviewStatistics`.
- New artifact `review-result.json` (appended to `ARTIFACT_NAMES`).
- Dedupe markers now use rewording-stable v2 keys and recognize existing v1 markers during the one-time dual-key transition.
- Added the narrow `ModelRunner` seam with `PiCliRunner`; `PiRunner` remains a deprecated compatibility alias for one release.
- PowerShell operations wrappers are deprecated compatibility entrypoints; use `python -m reviewforge.ops` for cross-platform container build and review commands.
- ADO pipeline fetch and post operations now execute in-process through `ado.operations`; `reviewforge.ado.cli` remains a legacy compatibility shim.
- Follow-up reviews now curate deterministic human feedback from prior bot threads and suppress dismissed re-raises while preserving explicit regressions.

### Fixed

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
