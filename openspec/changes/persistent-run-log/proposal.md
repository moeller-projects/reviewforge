## Why

Operators cannot inspect a complete ReviewForge run from its persisted artifacts because console output is not retained with the run.

## What Changes

- Persist a redacted chronological `run.log` beside `run-summary.json` for every full, review-only, and post-only run.
- Route ReviewForge, Pi subprocess, Git, and ADO helper output through the shared logger while retaining stderr output.
- Add `REVIEW_LOG_LEVEL` as an additive logging configuration variable.

## Capabilities

### New Capabilities
- `persistent-run-log`: Redacted per-run console logging artifact.

### Modified Capabilities
- None.

## Impact

- `src/reviewforge/runlog.py`, pipeline orchestration and stages, Pi runner, Git operations, ADO client, and artifact layout.
- Operator documentation and regression tests.
- No public JSON shape changes or dependency additions.
