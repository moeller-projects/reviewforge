## Context

The pinned Pi CLI 0.80.7 documents `--mode json`, but that changes stdout from the review JSON document to an event stream. Its verified help exposes no dedicated structured usage flag. ReviewForge must preserve its current JSON-producing command shape.

## Goals / Non-Goals

**Goals:**
- Detect absent stderr usage for successful non-empty Pi responses.
- Expose whether token data came from the stderr regex or is unavailable.
- Preserve the environment scrubber and read-only tool allowlist.

**Non-Goals:**
- Parsing Pi JSON event streams or changing Pi output mode.
- Changing metric keys, artifact filenames, or retry behavior.

## Decisions

- Keep the stderr parser as the sole source for this pinned CLI. `--mode json` is not a usage-only flag and would alter the response contract.
- Track `stderr-regex` when any invocation parses usage; otherwise expose `none`. Warn for each successful non-empty response without a usage line, and additionally when completed invocations have accumulated only zero values.
- Attach `token_usage_source` only to stage details with token usage, so `StageRecord.details` serializes it through the existing run-summary path.

## Risks / Trade-offs

- A valid Pi response can omit usage; the warning is intentional observability, not a failure.
- The regex remains version-coupled, but missing coverage becomes visible in logs and artifacts.
