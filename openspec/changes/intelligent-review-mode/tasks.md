## 1. State Discovery

- [x] 1.1 Add normalized review-mode enum, reviewer identity, thread, commit, and review-state models.
- [x] 1.2 Extend ADO context fetching with authenticated identity, normalized comments/threads, and source commit history.
- [x] 1.3 Implement conservative deterministic mode selection with force-full and ancestry safeguards.

## 2. Pipeline Integration

- [x] 2.1 Add review-state detection before reasoning and expose compact context through `StageContext`.
- [x] 2.2 Skip reasoning and prompt construction for NoOp with an informational final document.
- [x] 2.3 Narrow follow-up repository diffs when the reviewed commit is available; retain full-review fallback.
- [x] 2.4 Add `--force-full-review` and configuration wiring.

## 3. Verification

- [x] 3.1 Add unit tests for identity caching, mode selection, commit boundaries, thread classification, and fallback behavior.
- [x] 3.2 Add orchestration tests proving NoOp skips Pi and other modes execute once.
- [ ] 3.3 Run targeted tests, smoke checks, and OpenSpec validation.
