## Context

ReviewForge emits operator diagnostics to stderr from orchestration, stages, Pi, Git, and ADO helpers. Per-run artifacts persist structured output but not that chronology. Artifact files must not contain credential values.

## Goals / Non-Goals

**Goals:**
- Persist a redacted `run.log` in every run artifact directory while retaining stderr output.
- Keep `run-summary.json` unchanged as the machine-readable record.

**Non-Goals:**
- Change Python's root logger, add a logging dependency, or persist logs before an artifact directory exists.

## Decisions

- Use a named `reviewforge` stdlib logger with stderr and UTF-8 file handlers. This avoids global logging configuration and preserves console visibility.
- Configure once after artifact creation at each orchestration entrypoint. The artifact exists before the first persisted record.
- Redact configured credential environment values with a handler filter before either handler emits a record. This protects the artifact and console consistently.
- Add `run.log` at the end of `ARTIFACT_NAMES`; existing names and JSON records remain unchanged.

## Risks / Trade-offs

- Messages logged before orchestration configuration are not persisted; entrypoint validation failures have no run directory.
- Environment-value replacement cannot redact a secret that is absent from the process environment; credential-bearing integrations already source these variables.
