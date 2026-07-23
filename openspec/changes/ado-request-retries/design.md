## Context

ADO requests have a fixed 60-second socket timeout and no retry. Retrying a received 5xx after creating a thread could duplicate comments.

## Goals / Non-Goals

**Goals:** bounded exponential backoff with jitter, Retry-After support, transport retry safety, and configurable policy.

**Non-Goals:** retrying POST/PUT after an HTTP response or changing the socket timeout.

## Decisions

- GET retries 429/500/502/503/504 and transport failures.
- POST/PUT retry only URLError/TimeoutError because no status was received.
- Retry-After overrides jitter and is capped; the overall retry budget prevents unbounded waiting.

## Risks / Trade-offs

- Transport retry of a POST can still be ambiguous at the network boundary; marker dedupe remains the existing protection.
