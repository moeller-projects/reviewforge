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
