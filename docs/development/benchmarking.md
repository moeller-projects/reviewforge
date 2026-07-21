# Benchmarking

**Purpose:** compare reasoning paths using existing metrics. **Audience:** maintainers. **Mode:** how-to.

The implementation exposes comparable metrics through `ReviewResult.metrics` and `run-summary.json`, including Pi token counts, invocation and repair counts, wall-clock duration, and reasoning duration. Run equivalent PR inputs with `single_pi` and `multi_stage`, retain the artifact directories, and compare those fields.

There is no dedicated benchmark CLI or benchmark harness in the current repository. Use the normal CLI with deterministic `REVIEW_RUN_ID` and controlled session settings; do not document a nonexistent benchmark command.
