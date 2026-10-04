## 1. Configuration contract

- [x] 1.1 Add focused change proposal, design, and service-configuration delta spec.
- [x] 1.2 Record the section/key mapping and intentional removal of `ReviewForge:*` aliases.

## 2. Typed option sections and consumers

- [x] 2.1 Add and bind `WorkspaceOptions` and `PersistenceOptions`; migrate checkout, queue, store, retention, and workspace consumers.
- [x] 2.2 Add and bind `ReviewOptions`; migrate review pipeline and resolve-run consumers, including nested sharding settings.
- [x] 2.3 Add and bind `GitOptions` and `HostOptions`; migrate Git operations, hosted workers, stale-shell cleanup, telemetry setup, and the LLM governor's worker-count default.
- [x] 2.4 Remove `ReviewForgeServiceOptions` and `StoreOptions` after all consumers move; keep reusable options in appropriately named files.

## 3. Configuration contract migration

- [x] 3.1 Move all affected values in `config.toml` to the new sections while retaining defaults and root `Urls`.
- [x] 3.2 Update options-validation and service tests to use the new keys and verify binding, defaults, validation failures, worker registration, persistence, and overrides.
- [x] 3.3 Update README, AGENTS guidance, comments, examples, and other tracked references; remove obsolete configuration use except the explicit migration note and negative alias test.

## 4. Verification

- [ ] 4.1 Run affected test projects and a service startup smoke check using TOML plus an environment override, without restore unless separately authorized.
- [ ] 4.2 Validate this OpenSpec change.
