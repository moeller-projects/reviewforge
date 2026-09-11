## Why

Large pull requests are currently split primarily by file order and byte limits. This can separate files that participate in the same flow or API change, while the production `single_pi` engine reviews chunks sequentially. CRG already exposes changed functions, affected flows, impacted files, and architecture relationships that can improve scope selection.

## What Changes

- Add a deterministic CRG-aware review-scope planner using existing CRG outputs.
- Preserve existing byte limits as hard scope boundaries and fall back to current file-based chunking when CRG is unavailable.
- Use the shared scopes in both `single_pi` and legacy multi-stage review paths.
- Run independent scopes in bounded, isolated Pi sessions and synthesize results after all scopes finish.
- Record scope ownership and planning diagnostics in a new additive artifact.
- Fail the review when a required scope fails; do not publish partial review results.

## Capabilities

### New Capabilities
- `crg-aware-review-scopes`: Deterministic graph-informed scope planning and bounded parallel review execution.

## Impact

Scope planning, review orchestration, Pi session management, prompt composition, artifacts, configuration, tests, and OpenSpec validation. Existing finding schemas, dedupe behavior, and CRG fail-safe behavior remain compatible.
