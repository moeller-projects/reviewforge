## ADDED Requirements

### Requirement: Persist redacted per-run logs
The reviewer MUST create `run.log` in each full, review-only, and post-only run artifact directory. It MUST contain chronological ReviewForge, stage, Pi subprocess, Git, and ADO helper records while continuing to emit the same records to stderr.

#### Scenario: Review pipeline records a Pi diagnostic
- **WHEN** a configured review run executes a stage that receives a Pi stderr line
- **THEN** `run.log` MUST contain the stage start, Pi line, and stage finish in that order, and stderr MUST contain the Pi line.

### Requirement: Redact credential values
The run logger MUST replace values of `ADO_AUTH_TOKEN`, `ADO_MCP_AUTH_TOKEN`, `ADO_API_KEY`, `SYSTEM_ACCESSTOKEN`, and `OPENAI_API_KEY` with `***` before writing a record.

#### Scenario: Diagnostic contains configured token
- **WHEN** a log message includes the current `ADO_AUTH_TOKEN` value
- **THEN** `run.log` MUST contain `***` and MUST NOT contain the token value.

### Requirement: Preserve artifact compatibility
`run.log` MUST be appended to `ARTIFACT_NAMES` and the existing artifact names and `run-summary.json` shape MUST remain unchanged.

#### Scenario: Existing artifact consumer inspects run summary
- **WHEN** a run completes with persistent logging enabled by default
- **THEN** its `run-summary.json` MUST retain its established machine-readable fields while `run.log` is available as the separate human-readable record.
