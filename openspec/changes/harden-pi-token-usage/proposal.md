## Why

Pi token accounting currently depends on an unvalidated stderr format. A Pi CLI format change can silently record zero tokens and corrupt review metrics.

## What Changes

- Detect and warn when successful Pi responses do not include a parseable token-usage line.
- Record the token-usage source in stage details and `run-summary.json`.
- Preserve the existing Pi command shape, scrubbed environment, and read-only tool allowlist.

## Capabilities

### New Capabilities
- `pi-token-usage-observability`: Detect unavailable Pi token metrics and expose their source to review artifacts.

### Modified Capabilities
- None.

## Impact

- `src/reviewforge/ai/runner.py`
- `src/reviewforge/pipeline/stage.py`
- Runner and session-reuse tests
