# Design: Split service configuration into cohesive sections

## Section ownership

| Section / type | Settings |
|---|---|
| `Review` / `ReviewOptions` | `PromptOverridePath`, `RuleSetsPath`, `MaxContextTokens`, `MaxIterations`, `CleanRunVote`, `ReasoningEffort`, `AgentDebugLogging`, diff character/byte budgets and exclusions, `TrivialDiffSkipEnabled`, nested `Sharding` |
| `Workspace` / `WorkspaceOptions` | `WorkDir` and nested checkout eviction settings currently represented by `CheckoutEvictionOptions` |
| `Persistence` / `PersistenceOptions` | `StoreConnectionString`, `QueueMode`, journal mode, nested retention settings |
| `Git` / `GitOptions` | `TargetedFetchEnabled`, `MaxConcurrency` (formerly `GitMaxConcurrency`) |
| `Host` / `HostOptions` | `WorkerCount`, `StaleShellMinutes`, `OtlpEnabled` |

Independent option types remain in their current sections. `ApiKeyOptions` remains manually populated so API keys continue to come only from `REVIEWFORGE_API_KEYS`; `AdoOptions.Pat` continues to come only from `REVIEWFORGE_ADO_PAT`.

## Composition and validation

Bind each new section once and use its typed options for consumers. Composition-time decisions (queue registration, work-directory creation, hosted worker count, telemetry exporter enablement) must use the same values as the validated options. Preserve existing startup failure behavior for invalid queue mode, journal mode, worker count, sharding values, clean-run vote, and stale-shell/retention constraints. Update cross-option defaults so `Reasoning:MaxConcurrentRequests` continues to default to `Host:WorkerCount * 2`.

## Compatibility

The old `ReviewForge:*` configuration contract is intentionally removed. No alias or fallback is retained. TOML files, environment overrides, command-line overrides, and deployment examples must use the new keys. Existing defaults and unrelated sections remain stable. Root `Urls` remains at the root.

## Risks

- Deployments may depend on `ReviewForge__*` environment-variable names not tracked in the repository; release notes must call out the one-time migration.
- Direct `IConfiguration` reads currently determine queue, directories, worker count, and OTLP setup before options validation. The refactor must not let the typed options and composition-time decisions diverge.
- `ReviewForgeServiceOptions` currently also contains reusable types and sits beside `ReviewPipelineFactory`; extract the option types without moving unrelated pipeline construction behavior.
