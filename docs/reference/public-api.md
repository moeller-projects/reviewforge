# Public API

**Purpose:** identify supported Python entry points. **Audience:** integrators and contributors. **Mode:** reference.

Primary exports include:

- `reviewforge.cli.main()` and `build_parser()`.
- `reviewforge.config.Config`, `ConfigError`, and source constructors.
- `reviewforge.pipeline.orchestrator.run()`, `run_full()`, `run_review_only()`, `run_post_only()`, and `RunOutcome`.
- `reviewforge.pipeline.Stage`, `StageContext`, `StageResult`, `StageStatus`, and validators.
- `reviewforge.reasoning.ReasoningEngine`, `get_engine()`, and `register_engine()`.
- `reviewforge.pipeline.schemas.ReviewResult` and related Pydantic models.
- `reviewforge.ado.client.AdoClient`.
- `reviewforge.artifacts.manager.Artifacts`, `ARTIFACT_NAMES`, and `create()`.

The package also retains legacy helper exports and `reviewforge.ado.cli` commands for compatibility. They are current code paths, but new reasoning integrations should use the canonical engine and schema interfaces.
