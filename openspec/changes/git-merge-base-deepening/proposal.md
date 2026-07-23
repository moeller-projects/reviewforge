## Why

The fixed shallow-fetch fallback can still omit the merge base for long-lived or heavily rebased pull-request branches. That exposes a raw Git failure instead of completing a safe review range or reporting a diagnosable error.

## What Changes

- Iteratively deepen both pull-request refs from the initial shallow fetch through a bounded 10,000-commit depth.
- Fetch both refs without shallow limits if the bounded attempts still lack a merge base.
- Raise a `GitOperationError` naming the branches and attempted depths when no merge base is available after the fallback.

## Capabilities

### New Capabilities

- `merge-base-resolution`: Resolve a merge base reliably from shallow pull-request fetches.

### Modified Capabilities

- None.

## Impact

- `src/reviewforge/git/ops.py`: shallow-fetch and merge-base resolution.
- `tests/test_scripts_modules.py`: deterministic fetch-depth regression coverage.
