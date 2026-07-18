# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Added

- **Fast review mode** (`FAST_REVIEW=1` / `--fast-review`). Runs the entire Pi-driven portion of the review pipeline — intent reconstruction, context planning, context collection, context digest, diff review, finding verification, and severity calibration — in a single Pi call. Non-Pi stages (metadata fetch, repo preparation, AC coverage, posting) remain unchanged. See `docs/reference/pipeline.md` and `docs/reference/configuration.md` for details.
- New prompt file `prompts/fast-review-system.md`.
- New Pydantic schemas in `reviewforge.pipeline.schemas`: `FastReviewResult`, `ContextSummary`, `ReviewSummary`, `VerificationSummary`, `ReviewStatistics`.
- New `FastReviewStage` and pipeline variants `FAST_REVIEW_PIPELINE` / `FAST_REVIEW_REVIEW_ONLY_PIPELINE`.
