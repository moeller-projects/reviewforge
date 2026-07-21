# Prompt development

**Purpose:** change prompts without breaking model contracts. **Audience:** prompt maintainers. **Mode:** how-to.

1. Identify the engine and stage that loads the prompt.
2. Preserve the JSON-only output shape and field names required by `pipeline.schemas`.
3. Preserve scope, evidence, severity, and untrusted-content rules in the current system prompts.
4. Run the relevant reasoning and schema tests.
5. Run `Config.validate_files()` through `reviewforge validate-config` with the intended path overrides.

The default engine uses `prompts/fast-review-system.md`. The `multi_stage` engine uses `review-system.md`, `intent.md`, `context-plan.md`, `context-digest.md`, `verify-findings.md`, and `severity.md`. `ac-coverage.md` is loaded only when AC LLM re-check is enabled. Runtime prompt augmentation is implemented in `ai.prompts`; do not duplicate it in prompt files.
