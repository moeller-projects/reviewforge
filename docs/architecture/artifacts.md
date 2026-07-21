# Artifact architecture

**Purpose:** explain run output persistence. **Audience:** operators and tooling authors. **Mode:** explanation.

`artifacts.manager.create()` resolves a per-PR, per-run directory under `REVIEW_ARTIFACT_ROOT` (default `/workspace/artifacts`). The latest run is discoverable through the PR directory's `latest.txt`. The stable file contract is defined by `ARTIFACT_NAMES`.

Stages write inputs, intermediate reasoning documents, the canonical `review-result.json`, the projected `final-findings.json`, posting results, and `run-summary.json`. JSON writes use the artifact builder; summaries record stage status, timing, token usage, posting counts, skip reason, and exit code.

Artifacts are observability and interchange boundaries. Do not document a file unless it is in `ARTIFACT_NAMES` or explicitly produced by a current stage. See the complete [artifact reference](../reference/artifacts.md).
