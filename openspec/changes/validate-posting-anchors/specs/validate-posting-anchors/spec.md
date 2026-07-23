## ADDED Requirements

### Requirement: Validate projected finding anchors
The pipeline MUST validate every non-work-item finding with a file and line against the current unified diff before posting.

#### Scenario: Valid anchor
- **WHEN** a finding line is in the diff line set for its file
- **THEN** the finding MUST remain inline.

#### Scenario: Invalid anchor under downgrade policy
- **WHEN** a finding file or line is absent from the diff and `ANCHOR_POLICY=downgrade`
- **THEN** the pipeline MUST clear its file and line and set `anchorDowngraded` to true.

### Requirement: Deterministic invalid-anchor handling
The pipeline MUST honor `ANCHOR_POLICY` values `drop` and `off` without changing work-item or already-general findings.

#### Scenario: Drop policy
- **WHEN** an invalid inline finding is processed with `ANCHOR_POLICY=drop`
- **THEN** it MUST be excluded from the postable document and represented as a discarded canonical finding.
