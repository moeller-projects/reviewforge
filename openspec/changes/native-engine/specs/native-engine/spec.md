## ADDED Requirements

### Requirement: In-process native reasoning engine

The review pipeline MUST support a `native` reasoning engine that runs the review loop in-process with pydantic-ai, with no Pi subprocess and no `OPENAI_API_KEY`. The default model MUST authenticate via the OpenAI Codex/ChatGPT subscription (`openai-codex:` provider reading the configured credential file), and any other pydantic-ai model string MUST be accepted.

#### Scenario: Native engine selection

- **WHEN** `REASONING_ENGINE=native` is configured
- **THEN** `ExecuteReasoningEngineStage` selects the `native` engine and returns a validated `ReviewResult`, with `single_pi` and `multi_stage` still registered and selectable

#### Scenario: Subscription auth without an API key

- **WHEN** the native model string begins with `openai-codex:`
- **THEN** the engine builds a Codex provider backed by a credential source reading `NATIVE_CREDENTIAL_PATH`, and never falls back to `OPENAI_API_KEY`

### Requirement: Findings as validated tool calls

The native loop MUST collect findings through `record_finding` tool calls validated against `RichFinding` at emission time. A malformed finding MUST be reported to the model for correction without failing the run, and findings recorded before a loop failure MUST be retained.

#### Scenario: Malformed finding is retried

- **WHEN** a `record_finding` call violates the `RichFinding` schema
- **THEN** the tool returns the validation error text as its result, and the model may correct and re-call without aborting the loop

#### Scenario: Loop crash keeps partial findings

- **WHEN** the agent loop raises after one or more findings were recorded
- **THEN** the engine raises `ReasoningEngineError` and the recorded findings remain observable on the collector

### Requirement: Deliberate completion signal

The native loop MUST treat `task_done` as the deliberate-completion signal. A loop that ends without it MUST keep its findings but record "loop terminated without task_done" in `metrics.reviewDepth`.

#### Scenario: Loop ends without task_done

- **WHEN** the model produces the final narrative without first calling `task_done`
- **THEN** the engine returns the collected findings and marks the review depth as non-deliberate

### Requirement: Read-only tool surface

The native agent loop MUST expose no write, edit, or shell tool. File reading MUST come from the harness `FileSystem(read_only=True)` capability plus the read-only review-domain toolset.

#### Scenario: Tool surface inspection

- **WHEN** the native agent is constructed
- **THEN** its tool names are limited to read-only filesystem tools (`read_file`, `list_directory`, `search_files`, `find_files`, `file_info`) and `read_context`, `record_finding`, `record_uncertainty`, `task_done`

### Requirement: Secret files are not readable

The native agent loop MUST deny file reads of secret-adjacent paths (`.git/`, `.env`, `.env.*`, `.envrc`, `*.pem`, `*.key`, and `**/secrets*`) through the harness `FileSystem` `denied_patterns`, so model-driven reads cannot exfiltrate credentials.

#### Scenario: Secret read is denied

- **WHEN** the model requests `read_file` for a secret-adjacent path such as `.env.production`, `.envrc`, `server.pem`, `id_rsa.key`, or `config/secrets.yml`
- **THEN** the harness denies the read with a recoverable error, and the file contents are never returned to the model

### Requirement: Byte-identical single_pi prefix

The shared prompt prefix extraction MUST preserve `single_pi` output byte-for-byte.

#### Scenario: Prefix parity

- **WHEN** `_build_single_pi_prefix` is called with the default intro on an unchanged context
- **THEN** the produced prefix matches the historical single-pi text exactly

### Requirement: Rotated credential persistence

The Codex credential source MUST persist refreshed tokens back to the configured file with an atomic temp-write + rename and owner-only permissions (mode `0o600`), and MUST surface an actionable message (naming `codex login` and `NATIVE_CREDENTIAL_PATH`) on unreadable or malformed credentials.

#### Scenario: Refresh persists across runs

- **WHEN** the provider refreshes credentials
- **THEN** the credential source writes the new tokens atomically to the file so a later container run loads them

#### Scenario: Rotated tokens are written owner-only

- **WHEN** the provider refreshes credentials
- **THEN** the persisted credential file has mode `0o600` (owner read-write only)

#### Scenario: Missing or corrupt credentials

- **WHEN** the credential file is absent or malformed
- **THEN** the source raises a `UserError` directing the operator to run `codex login` or check `NATIVE_CREDENTIAL_PATH`

### Requirement: Configurable thinking effort

The native engine MUST accept a `NATIVE_THINKING` setting and forward it to the model as pydantic-ai `ModelSettings.thinking`. An unset value MUST leave the model at its provider default, and an invalid value MUST be rejected at configuration load.

#### Scenario: Thinking effort forwarded

- **WHEN** `NATIVE_THINKING` is set to a valid value (`true`/`false` or `minimal`/`low`/`medium`/`high`/`xhigh`)
- **THEN** the constructed agent's model settings carry that thinking level, and the model's default is used when the variable is unset

#### Scenario: Invalid thinking value rejected

- **WHEN** `NATIVE_THINKING` is set to an unsupported string
- **THEN** configuration loading fails with a `ConfigError` naming the accepted values
