## Context

The pipeline currently fetches simplified PR metadata and threads, prepares the full target-to-source diff, and then invokes the selected reasoning engine. ADO side effects are isolated in `reviewforge.ado.*`; Pi remains read-only. Review history is not persisted outside the PR, so the current run must derive state from ADO payloads and commit metadata.

## Goals / Non-Goals

**Goals:**

- Make review-mode selection deterministic and testable.
- Resolve identity and fetch/normalize review history once per run.
- Skip all Pi work for an unchanged rerun.
- Narrow the repository diff for a confident follow-up review.
- Preserve full-review safety for missing, ambiguous, rebased, or force-pushed history.

**Non-Goals:**

- No database, persistent cache, or new external dependency.
- No changes to the `ReasoningEngine` interface.
- No AI-based mode detection.
- No automatic resolution or mutation of existing ADO threads.

## Decisions

1. **Pure mode selector plus normalized state model.** Keep `ReviewMode`, `ReviewState`, and selection logic in a small orchestration module so unit tests do not require ADO or Pi. `ForceFull` is selected first, then no prior reviewer comment means `Initial`, then a missing/untrusted commit boundary means `ForceFull`, then equal commits means `NoOp`, otherwise `FollowUp`.
2. **Fetch normalized review state through the existing ADO helper boundary.** Extend `fetch-context` output with reviewer identity, normalized reviewer comments, thread status, and commit metadata. The pipeline consumes JSON artifacts and does not add direct ADO calls outside `reviewforge.ado.*`.
3. **Use commit timestamps only as a fallback boundary.** ADO comments do not reliably carry a reviewed commit id. When a prior reviewer comment has an explicit commit/iteration marker, use it; otherwise resolve the newest source commit at or before the latest reviewer comment timestamp. If that cannot be proven, use `ForceFull` rather than silently skipping or narrowing.
4. **Carry normalized context in `StageContext.extras`.** Existing engine and prompt contracts remain intact; engines receive a compact structured `review_state` object through the context and prompts. No raw ADO payloads are embedded.
5. **NoOp is a stage skip.** The mode detector writes an informational final document and sets `skip_reason`; the reasoning stage returns `SKIPPED`, so no prompt builder or Pi runner is called. Posting remains able to record the summary without creating comments.

## Risks / Trade-offs

- ADO history may omit commit linkage or timestamps; conservative full-review fallback costs tokens but prevents silent missed reviews.
- A follow-up diff requires the reviewed commit to exist in the shallow clone; repository preparation falls back to the normal merge-base range if it is unavailable.
- Existing callers constructing `Config` directly need the new force-full field to remain compatible; the CLI alias is additive.
