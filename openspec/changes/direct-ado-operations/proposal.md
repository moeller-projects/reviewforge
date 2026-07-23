## Why

Pipeline ADO fetch and posting currently route through a subprocess CLI boundary, adding avoidable process overhead and obscuring errors. The implementation already has reusable operation logic, so the pipeline should call it directly while preserving the legacy CLI contract.

## What Changes

- Add in-process `fetch_pr_context` and `post_findings` operation entrypoints.
- Rewire pipeline stages to call those entrypoints directly.
- Keep `python -m reviewforge.ado.cli` as a compatibility CLI.
- Remove the obsolete subprocess helper from the ADO client.
- Preserve artifact paths, JSON shapes, posting markers, and error behavior.

## Capabilities

### New Capabilities
- `direct-ado-operations`: In-process ADO workflows for pipeline stages with a compatibility CLI boundary.

### Modified Capabilities
- None.
