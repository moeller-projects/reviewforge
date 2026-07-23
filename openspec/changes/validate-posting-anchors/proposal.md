## Why

Model-produced file and line anchors can be absent from the current diff, creating misleading Azure DevOps inline comments.

## What Changes

- Validate projected anchors against the Python-computed unified diff before posting.
- Downgrade, drop, or disable handling through `ANCHOR_POLICY`.
- Record anchor outcomes without changing existing run-summary fields.

## Capabilities

### New Capabilities
- `validate-posting-anchors`: Deterministic pre-post anchor validation.

### Modified Capabilities
- None.

## Impact

Pipeline stages, configuration, summary diagnostics, documentation, and posting tests.