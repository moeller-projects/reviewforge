# Schemas

**Purpose:** document public JSON contracts. **Audience:** integrators and maintainers. **Mode:** reference.

Pydantic models live in `pipeline.schemas`. Literal values are `severity: nit|minor|major|blocker`, `confidence: high|medium|low`, and `contextBasis: diff-only|surrounding-code-read|full-module-review`.

## Canonical `ReviewResult`

Fields: `metadata`, `review_summary`, `verification_summary`, `pr_summary`, `findings`, `discarded_findings`, `good_practices`, `uncertainties`, `metrics`, and `review_confidence`. A supplied non-empty document must include `review_summary`.

`RichFinding` contains `title`, `observation`, `impact`, `recommendation`, `severity`, optional `confidence`, `file`, `line`, `contextBasis`, `regression` (default `false`), and `evidence`. Evidence requires at least one reference, a changed line or classification, and rationale.

`ReviewState.previousFeedback` contains deterministic entries with a normalized finding fingerprint, thread status, latest human reply (truncated), disposition (`dismissed`, `fixed`, or `unresolved`), and thread ID. `regression` may be true only when changed lines reintroduce a prior issue.

## Legacy and stage schemas

`ReviewDoc` is `{summary, findings}`; each legacy `Finding` has `title`, `message`, `severity`, optional location/confidence/context/suggestion, and `Evidence`. Other stage models are `Intent`, `ContextPlan`, `ContextDigest`, and `AcCoverageLlmResult`.

Validation helpers are `validate_payload()` and `load_and_validate()`. Pipeline validators additionally expose `validate_review_doc()`, `validate_postable_review_doc()`, and `validate_stage()`. See [artifacts](artifacts.md) for file placement and [public API](public-api.md) for import surfaces.
