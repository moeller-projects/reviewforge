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
