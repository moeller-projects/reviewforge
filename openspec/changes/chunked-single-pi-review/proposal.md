## Why

Oversized single-Pi reviews truncate unified diffs mid-hunk and omit available commit intent context.

## What Changes

- Add bounded commit subjects to single-Pi review context.
- Partition oversized Python-computed unified diffs into ordered file chunks and review them in one Pi session.
- Merge validated partial findings deterministically into the canonical review result.

## Capabilities

### New Capabilities
- `chunked-single-pi-review`: Commit-aware, coherent chunked review for oversized unified diffs.

### Modified Capabilities
- None.

## Impact

Single-Pi reasoning, schemas, configuration, prompt contract, documentation, and tests. No new dependencies or model tool permissions.
