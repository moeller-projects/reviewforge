## ADDED Requirements

### Requirement: Follow-up state contains deterministic human feedback
ReviewForge MUST classify bot-authored ADO threads into feedback entries with a normalized file/title fingerprint, status, latest human reply truncated to 500 characters, and disposition `dismissed`, `fixed`, or `unresolved`.

#### Scenario: Thread status determines disposition
- **WHEN** a bot-authored thread has status `wontFix`, `closed`, or `byDesign`
- **THEN** its feedback disposition MUST be `dismissed`; `fixed` or `resolved` statuses MUST be `fixed`; other statuses MUST be `unresolved`

### Requirement: Dismissed findings are suppressed before posting
The pipeline MUST remove a finding matching a dismissed feedback fingerprint unless the finding has `regression: true`, and MUST record the removal in `discarded_findings` with the thread identifier.

#### Scenario: Non-regression re-raise is filtered
- **WHEN** a new finding matches a dismissed fingerprint and `regression` is false
- **THEN** the finding MUST be removed before posting and recorded as previously dismissed

#### Scenario: Regression is preserved
- **WHEN** a matching finding has `regression: true`
- **THEN** it MUST remain eligible for normal posting and deduplication

### Requirement: Regression metadata is backward compatible
`RichFinding` MUST accept an optional `regression` boolean defaulting to false, and prompts MUST require true only when changed lines demonstrate reintroduction.

#### Scenario: Existing result remains valid
- **WHEN** a result omits `regression`
- **THEN** validation MUST succeed and the field MUST be false
