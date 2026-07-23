# Artifacts

**Purpose:** list stable per-run files. **Audience:** operators and integrations. **Mode:** reference.

Default location: `REVIEW_ARTIFACT_ROOT/pr-<PR_ID>/runs/<RUN_ID>/`, with the latest run recorded in `pr-<PR_ID>/latest.txt`.

Stable files from `ARTIFACT_NAMES`:

`metadata.json`, `diff.patch`, `changed-files.json`, `commits.txt`, `final-findings.json`, `posted-comments.json`, `run-summary.json`, `review-system.combined.md`, `work-items.json`, `threads.json`, and `review-result.json`.

`review-result.json` is the canonical engine output. `final-findings.json` is a write-once postable projection and the `reviewforge post --input` interchange shape. `run-summary.json` contains stage records, timing, token totals, posting counts, skip reason, and exit code.
