## ADDED Requirements

### Requirement: Rewording-stable marker key
ReviewForge MUST create new `prb:<key>` markers from normalized file, line, and title only. Title normalization MUST lowercase, remove punctuation, and collapse whitespace; severity and message MUST NOT affect the v2 key.

#### Scenario: Finding prose changes
- **WHEN** a rerun reports the same file, line, and title with changed severity or message
- **THEN** the v2 key MUST remain unchanged and the finding MUST not be posted twice.

### Requirement: Historical marker compatibility
ReviewForge MUST recognize both the v1 and v2 keys for a finding when scanning existing bot markers and deciding whether to post.

#### Scenario: Existing v1 comment
- **WHEN** a PR thread contains a valid historical v1 marker for a finding
- **THEN** a rerun MUST skip that finding and MUST NOT create a duplicate thread.

### Requirement: Marker and anchor stability
ReviewForge MUST preserve the existing bare and HTML marker formats and MUST continue to use stale reconciliation, rather than dedupe keys, to handle changed line anchors.

#### Scenario: Marker rendering
- **WHEN** ReviewForge posts a new finding
- **THEN** it MUST emit exactly one existing-format marker line carrying the v2 key.
