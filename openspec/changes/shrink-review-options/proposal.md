# Proposal

## Why

ReviewForge exposes several adjacent option sections whose settings are consumed together, while sharding adds a second review execution path despite being unused in production. Consolidating configuration around its owning sections reduces drift and removes the sharding path that can limit cross-file review context.

## What Changes

- **BREAKING** Remove `Review:Sharding` and its execution path, metrics, and tests; all reviews use the existing single-agent path with `MaxDiffChars` truncation.
- **BREAKING** Move grep budgets to `Review:GrepMaxMs` and `Review:GrepMaxLines`, and symbol usage enablement to `Review:Enrichment:SymbolUsageEnabled`.
- Emit startup warnings when legacy `RepoReadTools:GrepMaxMs`, `RepoReadTools:GrepMaxLines`, or `Review:Sharding` configuration is detected; legacy keys do not affect runtime behavior.
- Resolve worker, Git scheduler, and LLM governor concurrency defaults during options post-configuration; pass stage diff limits explicitly from options.
- Keep `VerifyFindings` and `SymbolUsageEnricher` enabled/configurable, and retain checkout eviction settings within `WorkspaceOptions`.

## Capabilities

### New Capabilities
- `review-configuration`: Configuration ownership, legacy-key migration behavior, computed defaults, and the single-path review behavior after sharding removal.

### Modified Capabilities
- None.

## Impact

Affected areas include Core review orchestration and telemetry, Service options and dependency wiring, Infrastructure concurrency options, configuration TOML, README migration guidance, and corresponding tests. Existing deployments using moved keys must migrate; the legacy-key warning is temporary and does not preserve old-key behavior.
