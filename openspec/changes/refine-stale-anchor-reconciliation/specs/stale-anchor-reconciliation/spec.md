## MODIFIED Requirements

### Requirement: Notify only actionable stale inline findings
ReviewForge MUST append a stale-anchor notification only when a bot-authored inline finding has a file and line anchor absent from the current diff and its thread is actionable. Threads with terminal status `fixed`, `wontFix`/`wontfix`, or `closed` MUST NOT receive a stale-anchor notification.

#### Scenario: Active stale finding
- **WHEN** a bot-authored inline thread has status `active` and its file/line anchor is absent from the current diff
- **THEN** ReviewForge MUST append one stale-anchor notification unless the matching stale marker already exists.

#### Scenario: Fixed stale finding
- **WHEN** a bot-authored inline thread has status `fixed` and its file/line anchor is absent from the current diff
- **THEN** ReviewForge MUST NOT append a stale-anchor notification.

#### Scenario: Rejected stale finding
- **WHEN** a bot-authored inline thread has status `wontFix` or `wontfix` and its file/line anchor is absent from the current diff
- **THEN** ReviewForge MUST NOT append a stale-anchor notification.

#### Scenario: Closed stale finding
- **WHEN** a bot-authored inline thread has status `closed` and its file/line anchor is absent from the current diff
- **THEN** ReviewForge MUST NOT append a stale-anchor notification.

### Requirement: Preserve stale-anchor safety and idempotency
ReviewForge MUST NOT annotate human-authored threads, general PR comments, file-level comments without a line anchor, or threads posted during the current run. A stale-anchor notification MUST carry the existing `prb-stale:<key>` marker and MUST preserve the original finding comment.

#### Scenario: Missing diff
- **WHEN** the current `diff.patch` artifact is unavailable
- **THEN** ReviewForge MUST skip stale-anchor reconciliation rather than treating every existing inline thread as stale.

#### Scenario: Repeated run
- **WHEN** an actionable stale thread already contains its matching `prb-stale:<key>` marker
- **THEN** ReviewForge MUST NOT append another stale-anchor notification.

### Requirement: Explain stale anchors accurately
The stale-anchor notification MUST state that the original line location is no longer present in the current diff and MUST NOT claim that the underlying finding has been disproven.

#### Scenario: Notification wording
- **WHEN** ReviewForge appends a stale-anchor notification
- **THEN** the notification MUST identify the current revision when available, preserve the original finding for context, and instruct reviewers to re-evaluate it against the new code.
