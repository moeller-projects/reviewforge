# Adding a reasoning engine

**Purpose:** add a selectable reasoning implementation. **Audience:** maintainers. **Mode:** how-to.

1. Implement `ReasoningEngine` in `src/reviewforge/reasoning/`.
2. Return a validated `ReviewResult` from `execute(ctx)`.
3. Register the class with `register_engine(name, class)` during package initialization.
4. Confirm `ExecuteReasoningEngineStage` can select it through `Config.reasoning_engine`.
5. Add tests for selection, output validation, artifact materialization, and failure behavior.
6. Add the engine name and behavior to the reference docs only after tests pass.

Do not bypass the canonical schema or write directly to posting-only shapes. `pipeline.projection` owns compatibility conversion.
