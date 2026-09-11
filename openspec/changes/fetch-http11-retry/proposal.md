## Why

Review preparation can fail repeatedly when an Azure DevOps Git connection resets during pack transfer. Retrying the same transport leaves the run exposed to the same HTTP negotiation failure.

## What Changes

- Retry failed `git fetch` commands with Git HTTP/1.1 after the initial attempt.
- Preserve the existing fetch depth, refs, authentication, and retry count.

## Impact

- `src/reviewforge/git/ops.py`: fetch retry command selection.
- `tests/test_git_ops.py`: regression coverage for the transport fallback.
