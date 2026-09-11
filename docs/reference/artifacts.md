# Artifacts

**Purpose:** list stable per-run files. **Audience:** operators and integrations. **Mode:** reference.

Default location: `REVIEW_ARTIFACT_ROOT/pr-<PR_ID>/runs/<RUN_ID>/`, with the latest run recorded in `pr-<PR_ID>/latest.txt`.

Known artifact names from `ARTIFACT_NAMES` (best-effort outputs may be absent):

`metadata.json`, `diff.patch`, `changed-files.json`, `commits.txt`, `final-findings.json`, `posted-comments.json`, `run-summary.json`, `review-system.combined.md`, `work-items.json`, `threads.json`, `review-result.json`, `sarif-findings.json`, `run.log`, `crg-analysis.json`, `graph-context.json`, `comment-replies.json`, `pi-invocations.json`, and `review-scopes.json`.

`review-result.json` is the canonical engine output. `final-findings.json` is a write-once postable projection and the `reviewforge post --input` interchange shape. `run-summary.json` is the machine-readable record of stage records, per-feature graph timings, optional `context_file_reads`, token totals, posting counts, skip reason, and exit code. `run.log` is the human-readable chronological record of the run. `comment-replies.json` records validated replies generated for existing bot threads and whether each was posted; under `DRY_RUN`, entries are drafts.

`sarif-findings.json` is an additive SARIF 2.1.0 projection of `review-result.json` for dashboards and code-scanning tools. It is written on a best-effort basis and never fails the review or changes ADO posting.

`crg-analysis.json` is the foundation code-review-graph output. `graph-context.json` is its additive model-context projection; when enabled it may contain `api_surface`, `flows`, and `architecture`, each with an independent status. API snapshots contain node/edge sets plus node metadata, not signatures. The disposable checkout may also contain `.reviewforge-context/` during reasoning; it is not an artifact and is removed with the checkout.
