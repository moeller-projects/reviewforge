## ADDED Requirements

### Requirement: Resolve shallow-fetch merge bases
The repository preparation stage MUST attempt to resolve the merge base after fetching each pull-request branch at depth 200. When unavailable, it MUST deepen both refs iteratively with fivefold growth, without exceeding a cumulative depth of 10,000 commits, and retry merge-base resolution after every paired fetch.

#### Scenario: Merge base appears after repeated deepening
- **WHEN** the initial shallow fetch and first deepening omit the merge base but the second deepening includes it
- **THEN** the stage MUST use the resolved merge base to construct the review range without an unshallow fetch

### Requirement: Report exhausted merge-base resolution
When bounded deepening does not find a merge base, the repository preparation stage MUST fetch both refs with `--unshallow` and retry. If no merge base remains available, it MUST raise `GitOperationError` that names both branches and the attempted depths.

#### Scenario: No shared history after full fetch
- **WHEN** merge-base resolution fails after all bounded depths and both unshallow fetches
- **THEN** the stage MUST fail with a clear `GitOperationError` rather than a raw Git subprocess error
