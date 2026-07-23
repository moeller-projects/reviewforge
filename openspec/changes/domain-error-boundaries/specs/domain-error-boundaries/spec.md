## ADDED Requirements

### Requirement: Library domain errors
ReviewForge library modules MUST raise `ReviewForgeError` subclasses rather than `SystemExit` for Git, Pi, ADO, orchestration input, and review-validation failures.

#### Scenario: Pi failure
- **WHEN** a Pi subprocess fails
- **THEN** the runner MUST raise a `PiExecutionError` carrying the existing error text and structured details.

### Requirement: Stable CLI translation
ReviewForge CLI boundaries MUST translate domain errors to the existing stderr message format and exit code 1.

#### Scenario: Domain error reaches CLI
- **WHEN** a domain error reaches a CLI command
- **THEN** the command MUST print its existing `[review][ERROR]` message and return 1.

### Requirement: Stage failure preservation
Pipeline stages MUST preserve failed stage records when a domain error occurs.

#### Scenario: Stage domain failure
- **WHEN** a stage raises a domain error
- **THEN** its `StageResult` MUST be failed with the existing error text.
