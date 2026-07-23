## ADDED Requirements

### Requirement: Pi token usage observability
ReviewForge MUST retain its existing Pi command shape and read-only security posture while recording `token_usage_source` as `stderr-regex` when Pi stderr yields a valid usage record and as `none` when it does not.

#### Scenario: Usage line is parsed
- **WHEN** a successful Pi invocation emits the supported token usage line on stderr
- **THEN** the runner MUST record the parsed token counts and expose `token_usage_source: "stderr-regex"` in the stage details and run summary.

#### Scenario: Usage line is absent
- **WHEN** a successful Pi invocation produces a non-empty response without a parseable usage line
- **THEN** the runner MUST emit a `[review][WARN]` log and expose `token_usage_source: "none"`.

### Requirement: Silent-zero detection
ReviewForge MUST warn when completed Pi invocations have no parsed token values, so a Pi output-format change cannot silently appear as zero token usage.

#### Scenario: All invocations lack usage
- **WHEN** one or more Pi invocations have completed and their accumulated parsed token values are all zero
- **THEN** the runner MUST emit a `[review][WARN]` log identifying the zero-usage condition.
