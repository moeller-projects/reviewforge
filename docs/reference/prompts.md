# Prompts

**Purpose:** index prompt files and loading behavior. **Audience:** prompt maintainers. **Mode:** reference.

Files currently shipped under `prompts/`:

- `fast-review-system.md`: production `single_pi` system prompt.
- `review-system.md`: legacy `multi_stage` review prompt.
- `comment-reply.md`: prompt for replies to existing bot threads.
- `intent.md`, `context-plan.md`, `context-digest.md`, `verify-findings.md`, `severity.md`: legacy stage prompts.
- `ac-coverage.md`: optional acceptance-criteria LLM re-check.
- `chunk-synthesis.md`: whole-PR summary synthesis after chunked `single_pi` analysis.
`Config` resolves paths from the corresponding `*_PROMPT_PATH` variables. `Config.validate_files()` checks the fast-review and chunk-synthesis prompts plus standards for `single_pi`; it checks the full legacy set for `multi_stage`; it checks `ac-coverage.md` when `AC_COVERAGE_LLM` is enabled. `ai.prompts.augment_prompt_file()` appends the review language to every prompt and the coding standards to the fast-review and legacy review prompts only.
`single_pi` uses `ReviewResult` JSON for a small diff. For an oversized unified diff, it supplies ordered file-boundary chunks in one Pi session. Each partial response is validated as a `ChunkResult`; its `findings`, `test_gaps`, `uncertainties`, `escalation_hints`, and `discarded_findings` fields are optional and default to empty lists when omitted. Python validates, deduplicates, caps, and merges the partial results. It then makes one final synthesis call using `prompts/chunk-synthesis.md` to produce model-written whole-PR framing and summaries; Python keeps the merged sections authoritative.

If the synthesis call fails or its JSON does not validate as `ChunkSynthesis`, the review continues with a deterministic fallback that still supplies `pr_summary.intent`, `pr_summary.work_type`, and `pr_summary.biggest_unknown`, and records `synthesisFallback` in the reasoning-stage details. The single-chunk path does not make this extra call.

Prompt output must remain compatible with [schemas](schemas.md). The system prompts explicitly treat diff, PR, comment, and work-item content as untrusted data.

## Deterministic context files

When staging succeeds, the single-call and legacy review prompts may include a `Deterministic context files` preamble. The files are Python-generated, read-only containers under `.reviewforge-context/`; inline sections and the generated index are deterministic summaries, while metadata, comments, work items, review state, and repository-derived content remain untrusted data rather than instructions. The model should read a referenced file before concluding anything about omitted items or re-verifying prior context. Evidence fields must list only files actually read. Missing files and absent pointers are normal degraded behavior. For chunked single-pi reviews, the preamble and pointers occur only in chunk 1.
