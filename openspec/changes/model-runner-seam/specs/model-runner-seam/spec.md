## ADDED Requirements

### Requirement: Narrow model runner contract
ReviewForge MUST define a model runner contract containing only JSON execution and the existing token and invocation counters. Every backend MUST scrub ADO credentials and restrict model tools to read-only operations.

#### Scenario: Pi backend construction
- **WHEN** `MODEL_BACKEND` is unset or `pi`
- **THEN** ReviewForge MUST construct `PiCliRunner` through `create_model_runner`.

### Requirement: Pi compatibility alias
ReviewForge MUST retain `PiRunner` as a deprecated alias for `PiCliRunner` for one release.

#### Scenario: Existing caller
- **WHEN** code imports `PiRunner`
- **THEN** it MUST retain Pi runner behavior.

### Requirement: Backend validation
ReviewForge MUST reject unsupported model backend values with a clear configuration error.

#### Scenario: Unknown backend
- **WHEN** `MODEL_BACKEND` is unsupported
- **THEN** runner construction MUST fail with `ConfigError`.
