# Pipeline

**Purpose:** document stage order and execution semantics. **Audience:** operators and contributors. **Mode:** explanation.

`run_stages()` invokes stages in order, captures timing and token usage, returns `StageResult` records, and stops after the first failed stage. A stage may return `skipped` through `should_run()`.

Pipelines declared in `pipeline/stages/__init__.py`:

- `DEFAULT_PIPELINE`: `FetchPrMetadataStage` -> `PrepareRepositoryStage` -> `ExecuteReasoningEngineStage` -> `ValidateAnchorsStage` -> `PostToAdoStage`.
- `REVIEW_ONLY_PIPELINE`: the same sequence without posting.
- `POST_ONLY_PIPELINE`: `FetchPrMetadataStage` -> `PostToAdoStage`.
- `FAST_REVIEW_PIPELINE` and `FAST_REVIEW_REVIEW_ONLY_PIPELINE`: compatibility aliases for the corresponding current lists.

The selected engine owns Pi-driven reasoning. The physical pipeline owns metadata, repository preparation, materialization, projection, and posting. `run_full`, `run_review_only`, and `run_post_only` create artifacts, run the relevant list, write `run-summary.json`, and return `RunOutcome`.

Review mode can skip inactive or draft PRs, or target branches outside `REVIEW_TARGET_BRANCHES`, unless `force_review` is enabled. `dry_run` and `--no-post` prevent posting while retaining generated artifacts.
