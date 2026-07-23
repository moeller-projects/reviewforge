# Artifact architecture

**Purpose:** explain run output persistence. **Audience:** operators and tooling authors. **Mode:** explanation.

`artifacts.manager.create()` resolves a per-PR, per-run directory under `REVIEW_ARTIFACT_ROOT` (default `/workspace/artifacts`). The latest run is discoverable through the PR directory's `latest.txt`. The stable file contract is defined by `ARTIFACT_NAMES`.

Stages write deterministic inputs, the canonical `review-result.json`, its write-once `final-findings.json` projection, posting results, `run-summary.json`, and `run.log`. `run-summary.json` is machine-readable; `run.log` is the redacted, human-readable chronological record. `multi_stage` may retain private fragment documents under `raw/` only when `DEBUG_INTERMEDIATES=1`; they are not stable artifacts.

Artifacts are observability and interchange boundaries. Do not document a file unless it is in `ARTIFACT_NAMES` or explicitly produced by a current stage. See the complete [artifact reference](../reference/artifacts.md).
