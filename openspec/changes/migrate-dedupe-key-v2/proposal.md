## Why

The current dedupe key includes model-generated message prose and recalibrated severity, so equivalent findings are reposted when wording drifts between runs.

## What Changes

- Emit a v2 marker key based on normalized location and title only.
- Recognize v1 and v2 keys while checking existing markers, preserving deduplication for historical bot comments.
- Keep marker syntax and stale-anchor reconciliation unchanged.

## Capabilities

### New Capabilities
- `dedupe-key-migration`: Versioned finding deduplication that prevents prose-only rerun duplicates while honoring historical markers.

### Modified Capabilities
- None.

## Impact

- `src/reviewforge/ado/posting.py` and posting CLI
- Posting, stale-reconciliation, and marker documentation tests
- `docs/reference/ado-integration.md` and `CHANGELOG.md`
