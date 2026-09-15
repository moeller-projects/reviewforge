## 1. ADO ground truth

- [x] 1.1 Add pull request iteration and iteration-changes endpoints to the ADO client.
- [x] 1.2 Fetch the latest-iteration changed-file set in `fetch-context` and write `pr-changed-files.json`.

## 2. Scoping and suppression

- [x] 2.1 Restrict the prepared review diff to the ADO PR file set, failing open when unavailable or empty.
- [x] 2.2 Drop findings on out-of-scope files in anchor validation and skip them at posting with `not_in_pr_changes`.
- [x] 2.3 Record the follow-up merge-guard fallback reason in prepare stage details.
- [x] 2.4 Use the current target tip for follow-ups when it is an ancestor of the source despite older merges.
- [x] 2.5 Add regression coverage for target merges and ambiguous unrelated merges.

## 3. Verification

- [x] 3.1 Add unit and end-to-end coverage for scoping, suppression, and fail-open behavior.
- [x] 3.2 Run the full test suite, complexity gate, and OpenSpec validation.
