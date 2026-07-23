## ADDED Requirements

### Requirement: Deterministic review mode selection

The orchestration layer MUST resolve the authenticated reviewer and select exactly one review mode before constructing any reasoning prompt. The modes MUST be `Initial`, `FollowUp`, `NoOp`, or `ForceFull`.

#### Scenario: Initial review

- **WHEN** the authenticated reviewer has no prior comments on the pull request and force-full review is disabled
- **THEN** the selected mode is `Initial` and the complete pull-request review range is used

#### Scenario: Follow-up review

- **WHEN** the authenticated reviewer has prior comments, a newer source commit is confidently identified, and force-full review is disabled
- **THEN** the selected mode is `FollowUp` and the review context identifies only commits and files introduced after the last reviewed commit

#### Scenario: No-op review

- **WHEN** the authenticated reviewer has prior comments and no newer source commit exists
- **THEN** the selected mode is `NoOp`, an informational summary is produced, and no reasoning prompt or Pi invocation occurs

#### Scenario: Forced full review

- **WHEN** force-full review is enabled
- **THEN** the selected mode is `ForceFull` regardless of review history and exactly one normal reasoning execution is permitted

### Requirement: Safe review-state fallback

The orchestration layer MUST fall back to `ForceFull` whenever reviewer identity, review timestamps, commit boundaries, or repository ancestry cannot be determined confidently. It MUST NOT silently skip a review because review history is incomplete.

#### Scenario: Missing review boundary

- **WHEN** prior reviewer comments exist but no trustworthy reviewed commit or comparable commit timestamp can be derived
- **THEN** the selected mode is `ForceFull` and the full pull-request range is reviewed

#### Scenario: Rebased or force-pushed branch

- **WHEN** the previous reviewed commit is not an ancestor of the current source commit
- **THEN** the selected mode is `ForceFull` and the normal merge-base range is reviewed

### Requirement: Structured follow-up context

The orchestration layer MUST provide normalized review state to the reasoning engine, including mode, reviewer identity, previous reviewer findings/comments, active threads, resolved threads, review timestamp, changed commits, and changed files. Raw ADO response payloads MUST NOT be required by the engine.

#### Scenario: Follow-up context

- **WHEN** mode selection returns `FollowUp`
- **THEN** the engine receives normalized prior-review context and the narrowed diff before its single reasoning execution

### Requirement: Force-full CLI override

The review CLI MUST accept `--force-full-review`, and the resulting configuration MUST force a complete reasoning execution without changing the `ReasoningEngine` abstraction.

#### Scenario: CLI override

- **WHEN** the review command includes `--force-full-review`
- **THEN** configuration enables the `ForceFull` mode override
