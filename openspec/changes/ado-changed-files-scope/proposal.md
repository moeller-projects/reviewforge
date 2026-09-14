## Why

PR 8914 showed that a locally computed review diff can include files the pull request does not touch (merged-in target history, stale shallow merge base). Findings on those files produce comments on code the branch author never changed.

## What Changes

- Fetch the PR changed-file list from the Azure DevOps iterations API during `fetch-context` and persist it as `pr-changed-files.json`.
- Restrict the prepared review diff to files ADO lists as changed in the PR; fail open when the list is unavailable or excludes everything.
- Drop findings bound to files outside the ADO PR file set in anchor validation, and skip them at the posting boundary with reason `not_in_pr_changes`; general and work-item findings are exempt.
- Record the follow-up merge-guard fallback reason in the prepare stage details.

## Impact

- `src/reviewforge/ado/client.py`: iteration and iteration-changes endpoints.
- `src/reviewforge/ado/operations.py`: `fetch_pr_changed_files`, fetch-context artifact, posting allowlist.
- `src/reviewforge/pipeline/stages/fetch_pr_metadata.py`: load the new artifact into stage extras.
- `src/reviewforge/pipeline/stages/prepare_repository.py`: diff scoping and new detail keys.
- `src/reviewforge/pipeline/stages/validate_anchors.py`: `out_of_scope` drop.
- `src/reviewforge/git/ops.py`: `range_fallback_reason` on `RepoState`.
- Tests: ADO client/operations, prepare stage, anchor validation, posting, and end-to-end scoping.
