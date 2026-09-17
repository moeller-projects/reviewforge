# Review boundaries

## ADDED Requirements

### Requirement: Pi session isolation
The reviewer MUST assign each review run a distinct default Pi session identity while preserving an explicitly configured session identity.

#### Scenario: Two runs of one pull request
- WHEN two review runs execute for the same pull request without an explicit `PI_SESSION_ID`
- THEN their Pi session identifiers differ by run and state from the first run is not reused by the second.

### Requirement: Provenance summary
The reviewer MUST record the selected review mode, range, fallback reason, and file-scope outcome in `run-summary.json` when those stages execute.

#### Scenario: Follow-up range with scoped files
- WHEN repository preparation selects a follow-up range and intersects it with the authoritative ADO file list
- THEN the run summary records the range mode/specification and scope status/counts.

### Requirement: Contradictory file scope
The reviewer MUST fail preparation rather than review a local diff whose files have no intersection with a non-empty authoritative ADO changed-file list.

#### Scenario: ADO and local scopes disagree
- WHEN ADO returns a non-empty changed-file list and no local diff file is present in that list
- THEN preparation raises an input error and MUST NOT pass the unscoped local diff to the reasoning engine.

### Requirement: Strict single-pi anchors
The `single_pi` engine MUST discard file/line findings whose line is absent from the prepared diff, regardless of the legacy downgrade policy.

#### Scenario: Finding cites an unchanged line
- WHEN `single_pi` returns a finding with a file and line not present in the prepared diff
- THEN validation discards the finding and records the anchor discard.

### Requirement: Line-stable semantic markers
Finding deduplication MUST use normalized file and title identity without the mutable line anchor, while recognizing legacy and feedback markers.

#### Scenario: Finding moves after a rebase
- WHEN a rerun reports the same normalized file/title at a different line
- THEN posting recognizes the existing marker and does not create a duplicate thread.
