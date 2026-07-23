## Context

`prb:<key>` is a stable marker contract. v1 hashes file, line, severity, title, and message; message and severity drift across model reruns. Existing historical v1 markers must remain effective.

## Goals / Non-Goals

**Goals:**
- Emit v2 keys from normalized file, line, and title.
- Skip a finding when either its v1 or v2 key exists.
- Preserve marker syntax and stale-comment reconciliation.

**Non-Goals:**
- Rewriting existing PR comments or adding multiple markers to a comment.
- Using a dedupe key to reconcile changed line anchors.

## Decisions

- Keep `dedupe_key` as the public v2 producer and add a v1 helper solely for the mandatory transition check.
- Normalize title with lowercase, punctuation removal, and whitespace collapsing; exclude severity and message.
- Continue scanning marker values with the unchanged marker regex. It is key-format agnostic and accepts bare and HTML forms.

## Risks / Trade-offs

- Two distinct findings on the same location with punctuation-only title differences intentionally dedupe; this is the requested semantic identity.
- v1 compatibility persists until historical comments naturally age out; no marker layout changes are required.
