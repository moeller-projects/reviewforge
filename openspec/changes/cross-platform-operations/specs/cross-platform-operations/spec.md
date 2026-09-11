## ADDED Requirements

### Requirement: Shared operation version pins
The repository MUST keep PI, uv, and default model pins in one checked-in env-format file. Python operations and Azure Pipelines MUST fail clearly when that file is missing, and every container build MUST receive its PI and uv values from that file.

#### Scenario: Missing pin file
- **WHEN** a build or CI pin check cannot read the checked-in pin file
- **THEN** it MUST fail instead of selecting an embedded fallback version

### Requirement: Platform-neutral container operations
The repository MUST provide Python commands for image build, one-review container execution, and discovered batch-review execution. Explicit command options MUST override process environment values, which MUST override documented defaults; runs MUST forward the requested env file to the container.

#### Scenario: Preview a review container command
- **WHEN** an operator invokes the single-review command with explicit values and command preview enabled
- **THEN** it MUST print the container command with explicit values overriding environment values without spawning a container

### Requirement: Interactive batch selection by pull-request ID
The `run-open-prs` command MUST allow interactive operators to select discovered pull requests by exact pull-request ID in addition to positional indexes and inclusive index ranges. The command MUST preserve discovery order, deduplicate repeated selections, and fail clearly for malformed selectors or unknown pull-request IDs.

#### Scenario: Select discovered pull requests by ID
- **WHEN** interactive input contains one or more exact pull-request IDs from the displayed candidate set
- **THEN** the command MUST review those pull requests in displayed order

#### Scenario: Preserve existing index selection
- **WHEN** interactive input contains `all`, `none`, positional indexes, or inclusive index ranges
- **THEN** the command MUST retain the existing selection behavior

#### Scenario: Reject an unknown ID
- **WHEN** interactive input contains a numeric pull-request ID that is not in the displayed candidate set
- **THEN** the command MUST fail with a clear selection error instead of silently reviewing a different pull request

### Requirement: Container retention lifecycle
The container run command MUST always run detached and MUST include `--rm` unless `--keep-container` is enabled. Enabling `--keep-container` MUST suppress only automatic removal and MUST NOT change detached execution.

#### Scenario: Default container cleanup
- **WHEN** an operator runs a review without `--keep-container`
- **THEN** the generated command MUST contain both `--rm` and `-d`

#### Scenario: Retain a stopped container
- **WHEN** an operator runs a review with `--keep-container`
- **THEN** the generated command MUST contain `-d` and MUST NOT contain `--rm`
