# Tasks

## 1. Remove review sharding

- [x] 1.1 Remove the shard planner, review-stage sharding path, shard telemetry, and shard-specific tests.
- [x] 1.2 Ensure oversized diffs continue through the single-pass `MaxDiffChars` truncation path.

## 2. Consolidate configuration ownership

- [x] 2.1 Move grep budgets and symbol-usage enablement under `ReviewOptions`; preserve independent finding verification and workspace checkout-eviction settings, keeping the Core-owned eviction policy type in Core.
- [x] 2.2 Update dependency wiring, configuration files, and README migration guidance for the new keys and legacy-key warnings.

## 3. Resolve defaults before consumption

- [x] 3.1 Move worker, Git scheduler, and LLM concurrency defaults into options post-configuration.
- [x] 3.2 Pass configured diff limits at stage composition sites and remove constructor defaults that duplicate configuration.

## 4. Validate the change

- [x] 4.1 Update option, pipeline, telemetry, and configuration tests to cover new keys and legacy warnings.
- [x] 4.2 Run the authorized build-and-test script and validate the OpenSpec change.
