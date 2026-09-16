## Why

ReviewForge's reasoning currently shells out to the Pi coding-agent CLI for every review. An in-process engine removes the subprocess boundary, and pydantic-ai validates model output directly against the existing Pydantic v2 schemas. The OpenAI Codex/ChatGPT subscription (`openai-codex:` provider) authenticates through `codex login` OAuth instead of an API key.

## What Changes

- Add a third reasoning engine, `native`, that runs the review loop in-process with pydantic-ai + pydantic-ai-harness.
- Emit findings as validated `record_finding` tool calls during the loop, and keep the final structured output to a small `ReviewNarrative` (summaries only).
- Provide read-only file access through the harness `FileSystem(read_only=True)` capability and review-domain tools (`read_context`, `record_finding`, `record_uncertainty`, `task_done`) through a new `ReviewCollectorToolset`.
- Add Codex subscription OAuth credential loading/persistence (`~/.codex/auth.json`) with atomic rotated-token writes.
- Add native configuration (`NATIVE_MODEL`, `NATIVE_CREDENTIAL_PATH`, `NATIVE_MAX_TURNS`, `NATIVE_MAX_CONTEXT_TOKENS`, `NATIVE_READ_MAX_LINES`, `NATIVE_REVIEW_PROMPT_PATH`) and a `prompts/native-review-system.md` system prompt.
- Extract the shared prompt prefix from `single_pi` into `reasoning/prefix.py`; `single_pi` output stays byte-identical.
- Mount the Codex credential file into containers for native+codex runs.

## Capabilities

### New Capabilities

- `native-engine`: In-process pydantic-ai reasoning engine with subscription OAuth auth and tool-call-based finding collection.

### Modified Capabilities

- `reasoning-engine`: adds a third registered engine; registry and selection mechanism unchanged.

## Impact

The change touches reasoning engine registration, configuration, pipeline schemas, prompt files, container operations, tests, and documentation. `single_pi` and `multi_stage` remain registered and selectable; the default engine is unchanged. No write/edit/shell tools exist in the native loop. Backend MCP tools will later attach through pydantic-ai's MCP capability; no backend is implemented here.
