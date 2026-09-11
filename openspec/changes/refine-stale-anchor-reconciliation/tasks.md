## 1. Stale-anchor eligibility

- [x] 1.1 Filter terminal thread statuses from stale candidate detection.
- [x] 1.2 Preserve active-thread, marker, anchor, current-run, and idempotency behavior.

## 2. Notification and documentation

- [x] 2.1 Revise notification wording to describe stale anchors accurately.
- [x] 2.2 Document `ANNOTATE_STALE` and fail-closed missing-diff behavior.

## 3. Verification

- [x] 3.1 Add regression coverage for active, fixed, wontFix, closed, and missing-diff cases.
- [x] 3.2 Run focused tests, full suite, complexity gate, and OpenSpec validation.
