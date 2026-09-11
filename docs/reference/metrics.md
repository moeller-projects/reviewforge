# Metrics

**Purpose:** define recorded review metrics. **Audience:** operators and benchmark authors. **Mode:** reference.

`ReviewMetrics` records `changedFilesReviewed`, `filesIgnored`, `testsRead`, `symbolsInspected`, `workItemsRead`, `confidence`, `reviewDepth`, Pi input/output/total tokens, `invocationCount`, `repairInvocationCount`, `wallClockDurationMs`, `reasoningDurationMs`, `projectionDurationMs`, `validationDurationMs`, and optional `estimatedCost`. `metrics.confidence` mirrors `review_confidence.level`, and `testsRead`/`symbolsInspected`/`workItemsRead` are raised to evidence-derived lower bounds.

`ReviewMetadata` separately records start/end timestamps, duration, model name, reasoning engine, and `TokenUsage`. Stage records in `run-summary.json` contain stage name, status (`ok`, `skipped`, or `failed`), timestamps, duration, details, error, and token usage.

The multi-stage engine aggregates stage and worker token usage. Metrics are serialized with aliases where defined and are intended for observability and comparison, not billing guarantees.
