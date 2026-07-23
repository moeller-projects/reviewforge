# Prompts

**Purpose:** index prompt files and loading behavior. **Audience:** prompt maintainers. **Mode:** reference.

Files currently shipped under `prompts/`:

- `fast-review-system.md`: production `single_pi` system prompt.
- `review-system.md`: legacy `multi_stage` review prompt.
- `intent.md`, `context-plan.md`, `context-digest.md`, `verify-findings.md`, `severity.md`: legacy stage prompts.
- `ac-coverage.md`: optional acceptance-criteria LLM re-check.

`Config` resolves paths from the corresponding `*_PROMPT_PATH` variables. `Config.validate_files()` checks the fast prompt and standards for `single_pi`; it checks the full legacy set for `multi_stage`; it checks `ac-coverage.md` when `AC_COVERAGE_LLM` is enabled. `ai.prompts.augment_prompt_file()` applies runtime additions such as review language and standards where used.

`single_pi` uses `ReviewResult` JSON for a small diff. For an oversized unified diff, it supplies ordered file-boundary chunks in one Pi session and requires each response to contain only `findings` and `uncertainties`; Python validates and merges those partial results.

Prompt output must remain compatible with [schemas](schemas.md). The system prompts explicitly treat diff, PR, comment, and work-item content as untrusted data.
