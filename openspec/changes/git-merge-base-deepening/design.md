## Context

Repository preparation fetches both pull-request refs at depth 200. A single fixed deepen is insufficient when the merge base is older than that history.

## Goals / Non-Goals

**Goals:**
- Find merge bases available through progressive shallow fetches.
- Bound shallow history at 10,000 commits before a full-fetch fallback.
- Surface exhausted resolution as `GitOperationError` with actionable context.

**Non-Goals:**
- Change the review range, artifact contracts, or Git authentication.

## Decisions

- Deepen both refs by 1,000 commits, then multiply the increment by five; cap cumulative depth at 10,000. This limits network requests while covering deep histories.
- Use paired `--unshallow` fetches only after the cap. A full fetch is the correctness fallback, not the common path.
- Probe `merge-base` after every paired fetch and raise a domain error only after the final probe. This prevents leaking raw subprocess failures.

## Risks / Trade-offs

- Full fallback can transfer substantial history, but it runs only when bounded shallow history cannot determine the review range.
