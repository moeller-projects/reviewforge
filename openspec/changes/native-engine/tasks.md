## 1. Dependencies

- [x] 1.1 Add `pydantic-ai-slim[openai,mcp]>=2.41.0` and `pydantic-ai-harness>=0.31.0` to `pyproject.toml`; run `uv lock`.
- [x] 1.2 Add a Dockerfile smoke assertion for `prompts/native-review-system.md`.

## 2. Configuration

- [x] 2.1 Add `native_model`, `native_credential_path`, `native_max_turns`, `native_max_context_tokens`, `native_read_max_lines`, and `native_review_prompt_path` fields with env sourcing.
- [x] 2.2 Include the native prompt in `Config.validate_files()` for `reasoning_engine == "native"`.

## 3. Schema

- [x] 3.1 Add `ReviewNarrative` to `pipeline/schemas.py` and export it.

## 4. Shared prefix

- [x] 4.1 Extract `_build_single_pi_prefix` and helpers into `reasoning/prefix.py`; update `single_pi`, `pipeline.crg.prompt`, and tests.
- [x] 4.2 Preserve byte-identical `single_pi` prompt output (guarded by `test_prefix_matches_historical_single_pi_text`).

## 5. Toolset and engine

- [x] 5.1 Implement `ReviewCollectorToolset` (`read_context`, `record_finding`, `record_uncertainty`, `task_done`) in `reasoning/native_tools.py`.
- [x] 5.2 Implement `NativeReasoningEngine` (`resolve_native_model`, `CodexFileCredentialSource`, agent loop, `ReviewResult` assembly) in `reasoning/native.py`.
- [x] 5.3 Register `native` and export it.

## 6. Prompts and container

- [x] 6.1 Write `prompts/native-review-system.md`.
- [x] 6.2 Add `NATIVE_` env allowlist prefix and the Codex credential bind-mount (selected on `REASONING_ENGINE`) to `reviewforge/ops.py`.

## 7. Tests

- [x] 7.1 Write `tests/test_native_engine.py` (happy path, invalid-finding retry, missing task_done, read-only surface, secret-read denial, usage mapping, crash partial findings, context reads, registration, model resolution, credential source, owner-only credential mode, prefix parity).
- [x] 7.2 Run targeted native-engine tests.
- [x] 7.3 Run the full suite with coverage (`--cov-fail-under=97`) and the complexipy gate.

## 8. Docs

- [x] 8.1 Update `docs/architecture/reasoning-engine.md`, `docs/reference/environment-variables.md`, `docs/reference/schemas.md`, `docs/guides/running-reviews.md`, `docs/guides/operations.md`, and `CHANGELOG.md`.
- [x] 8.2 Add this OpenSpec change scaffold.
