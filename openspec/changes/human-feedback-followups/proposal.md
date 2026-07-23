## Why

Follow-up reviews currently see prior bot comments but do not distinguish findings humans dismissed from issues they fixed. This causes repeated noise and misses genuine regressions when changed code reintroduces an addressed issue.

## What Changes

- Curate deterministic feedback entries from bot-authored ADO threads.
- Include dispositions and feedback in review-state context and reasoning prompts.
- Add an additive `regression` field to rich findings.
- Suppress non-regression re-raises of dismissed findings before posting and record them as discarded.
- Preserve regression findings and existing marker/posting behavior.

## Capabilities

### New Capabilities
- `human-feedback-followups`: Deterministic feedback curation and regression-aware follow-up reviews.

### Modified Capabilities
- None.
