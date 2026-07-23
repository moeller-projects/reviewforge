## Context

Stages use only JSON execution and token/invocation counters, while Pi CLI flags, sessions, environment scrubbing, and repair behavior are backend-specific.

## Goals / Non-Goals

**Goals:** isolate Pi protocol behind the current minimal call surface; keep one shipped backend.

**Non-Goals:** plugin discovery, backend registration, or a second model backend.

## Decisions

- `ModelRunner` contains only `run_json` and four existing read-only counters.
- `PiCliRunner` owns all Pi mechanics; `PiRunner` is a one-release compatibility alias.
- `create_model_runner` selects only `MODEL_BACKEND=pi`; orchestration uses the factory.

## Risks / Trade-offs

- Concurrent Pi session isolation remains Pi-specific in the existing parallel stages.
