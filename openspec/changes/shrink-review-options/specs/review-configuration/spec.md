# Spec Delta

## Purpose
Define the supported review configuration contract and the observable behavior when deployments use legacy keys or large diffs.

## ADDED Requirements

### Requirement: Reviews MUST use one bounded-context analysis path
The service MUST review each pull request through one reasoning pass using the configured maximum diff size. It MUST NOT split diffs into shards or expose shard-specific execution telemetry.

#### Scenario: Large diff exceeds the configured context limit
- **WHEN** a pull request diff exceeds `Review:MaxDiffChars`
- **THEN** the review uses the existing single-pass truncation behavior and does not create shard runs

### Requirement: Review-owned budgets and enrichment settings MUST bind under Review
The service MUST read grep budgets from `Review:GrepMaxMs` and `Review:GrepMaxLines`, and symbol usage enablement from `Review:Enrichment:SymbolUsageEnabled`. `VerifyFindings` MUST remain independently configurable and retain its current behavior.

#### Scenario: New configuration keys are provided
- **WHEN** the service starts with the supported `Review` keys configured
- **THEN** grep and symbol-usage behavior uses those values and finding verification remains separately controlled

### Requirement: Legacy configuration keys MUST be reported
The service MUST emit a startup warning when legacy `RepoReadTools:GrepMaxMs`, `RepoReadTools:GrepMaxLines`, or `[Review.Sharding]` configuration is present. Those legacy keys MUST NOT control runtime behavior.

#### Scenario: Legacy grep budgets are present
- **WHEN** the service starts with either legacy `RepoReadTools` grep budget configured
- **THEN** it emits a migration warning identifying `Review:GrepMaxMs` and `Review:GrepMaxLines` and binds only the new keys or their defaults

#### Scenario: Legacy sharding configuration is present
- **WHEN** the service starts with `[Review.Sharding]` configuration
- **THEN** it emits a migration warning and runs without sharding

### Requirement: Computed concurrency defaults MUST be resolved before options are consumed
The service MUST expose fully resolved worker-count, Git-operation concurrency, and LLM concurrency values through their options after configuration, using the existing automatic defaults when explicit values are absent.

#### Scenario: Concurrency is not explicitly configured
- **WHEN** the options are resolved without an explicit concurrency value
- **THEN** each options value contains the existing computed automatic default before dependent services consume it

### Requirement: Diff limits MUST come from review configuration
Review stages MUST receive their diff limit explicitly from the configured review options rather than relying on constructor argument defaults.

#### Scenario: Stage is composed with configured diff limit
- **WHEN** the service constructs a review stage
- **THEN** the stage receives the configured `Review:MaxDiffChars` value
