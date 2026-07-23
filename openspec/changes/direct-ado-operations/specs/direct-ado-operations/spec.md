## ADDED Requirements

### Requirement: Pipeline ADO workflows execute in-process
The pipeline MUST invoke dedicated in-process operations for fetching PR context and posting findings, without spawning the legacy CLI as a subprocess.

#### Scenario: Fetch stage invokes the direct operation
- **WHEN** the fetch metadata stage runs for a configured pull request
- **THEN** it MUST call the direct fetch operation and write the existing context artifacts to the configured artifact directory

#### Scenario: Post stage invokes the direct operation
- **WHEN** the post stage runs with final findings and posting is enabled
- **THEN** it MUST call the direct post operation and preserve the existing `posted-findings.json` artifact shape

### Requirement: Legacy ADO CLI remains compatible
The package MUST continue to support `python -m reviewforge.ado.cli fetch-context` and `post-findings` for external callers.

#### Scenario: Legacy fetch command runs
- **WHEN** an external caller invokes the fetch-context CLI with valid arguments
- **THEN** it MUST produce the same context artifacts and exit semantics as before

#### Scenario: Legacy post command runs
- **WHEN** an external caller invokes the post-findings CLI with valid arguments
- **THEN** it MUST produce the same posting result artifact and exit semantics as before
