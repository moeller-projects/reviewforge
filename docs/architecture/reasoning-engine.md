# Reasoning engine

**Purpose:** explain engine selection and contracts. **Audience:** contributors extending review reasoning. **Mode:** explanation.

`ReasoningEngine` is an abstract interface with a stable `name` and `execute(StageContext) -> ReviewResult`. `register_engine()` stores implementations in a process-local registry; `get_engine()` instantiates by configured name and raises `ValueError` for unknown names.

Built-ins:

- `single_pi`: default production engine; one Pi-driven review call produces the canonical result.
- `multi_stage`: runs `BuildArtifactsStage`, intent reconstruction, context planning/collection/digest, diff review, verification, severity calibration, and acceptance-criteria coverage before constructing `ReviewResult`.

`ExecuteReasoningEngineStage` selects `ctx.cfg.reasoning_engine`, stores `review-result.json`, projects the result to `final-findings.json`, and records metrics. `FAST_REVIEW` and `--fast-review` are compatibility aliases for `single_pi` only when no explicit reasoning engine is configured. The selected engine does not automatically fall back to another engine.

To add an engine, implement the abstract interface, register it during package initialization, add configuration acceptance, and test the observable result and selection path. See [adding an engine](../guides/adding-a-reasoning-engine.md).
