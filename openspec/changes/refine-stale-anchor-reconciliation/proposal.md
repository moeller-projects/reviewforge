## Why

ReviewForge currently appends stale comments to inline bot threads whose anchors disappeared from the current diff, even when Azure DevOps already marks the thread `fixed` or `wontFix`. PR 8884 demonstrates that these notifications add noise and can instruct reviewers to resolve threads that are already resolved.

## What Changes

- Restrict stale-anchor notifications to actionable unresolved bot-authored inline threads.
- Skip terminal thread states including `fixed`, `wontFix`, and `closed`.
- Clarify that the notification reports an obsolete line anchor, not a disproven finding.
- Preserve marker-based idempotency, best-effort posting, existing artifact fields, and the `ANNOTATE_STALE` compatibility control.
- Document fail-closed behavior when the current diff is unavailable.

## Capabilities

### Modified Capabilities
- `stale-anchor-reconciliation`: Only actionable unresolved inline findings receive stale-anchor notifications.

## Impact

Posting reconciliation logic, stale-reconciliation tests, ADO integration documentation, and configuration documentation. No changes to dedupe markers, thread closure, or finding validation.
