# Implementation progress: ADO system-event filtering

Branch: `feature/ignore-ado-system-events` (isolated worktree, based on `main`)

## Decisions and rulings
- System vote/status comments are not human input; preserve origin as Human/Bot/System.
- Shared Lukas identity: ReviewForge preamble means Bot; plain text means Human. Unknown non-system text is Human.
- Ignore system events when finding the latest relevant comment, rather than treating a system event after a human/bot comment as the new thread reply. This preserves genuine human commands/replies and bot retry deduplication.
- Keep timestamp watermark; calculate it from Human comments only. No ID cursor absent evidence of out-of-order ADO delivery. Existing persisted timestamps remain readable as legacy watermarks.
- Avoid redundant reviewer vote writes when current vote matches desired. ADO reviewer state is read in the adapter before the write; if reviewer data is unavailable, keep the requested write rather than silently skipping it.
- No separate summary dedupe: system events should not trigger the repeat runs that create redundant summaries/replies.
- No test/build/restore commands without explicit authorization; user authorized implementation, not those commands.

## Task ledger
- T-01 comment origin model and ADO classification: implemented; classification tests added; execution unverified.
- T-02 human-only review/resolve/pending-reply semantics: implemented; discovery, gate, command, collector, and bot-reply retry tests added; execution unverified.
- T-03 human-comment watermark, including legacy compatibility: implemented in review/draft persistence; tests added; execution unverified.
- T-04 vote-write idempotence and end-to-end regression coverage: implemented in ADO adapter; decision helper and same-head discovery tests added; execution unverified.
- Whole-branch review: review round 1 found a mixed-thread edge case; fixed by selecting the latest non-System comment throughout pending-reply, command, triage, and retry-dedup paths, and excluding system comments from resolve-agent history. Review round 2 approved with no findings.

## Verification
- `git diff --check`: passed after final edits.
- Source search found no remaining `!IsBot` human-comment predicates in Core or Service.
- Tests/build/restore: not run — not authorized.
- Rulings: ADO vote comparison remains in the ADO adapter, using its existing pull-request reviewer data; absent reviewer entry means neutral vote (0), while a missing reviewer collection is treated as unavailable and does not suppress the write.
