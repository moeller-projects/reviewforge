# Reasoning engine

**Purpose:** explain engine selection and contracts. **Audience:** contributors extending review reasoning. **Mode:** explanation.

`ReasoningEngine` is an abstract interface with a stable `name` and `execute(StageContext) -> ReviewResult`. `register_engine()` stores implementations in a process-local registry; `get_engine()` instantiates by configured name and raises `ValueError` for unknown names.

Built-ins:

- `single_pi`: default production engine; one Pi-driven review call produces the canonical result.
- `multi_stage`: runs `BuildArtifactsStage`, intent reconstruction, context planning/collection/digest, diff review, verification, severity calibration, and acceptance-criteria coverage before constructing `ReviewResult`.
- `native`: in-process pydantic-ai agent loop (no Pi subprocess). The model authenticates via the OpenAI Codex/ChatGPT subscription (`openai-codex:` provider, OAuth from `~/.codex/auth.json`) by default. It uses the harness `FileSystem(read_only=True)` capability for sandboxed reads plus a `ReviewCollectorToolset` whose `record_finding` / `record_uncertainty` / `task_done` calls are validated per emission. The loop's final structured output is only the small `ReviewNarrative` (summaries); findings are assembled from the collected tool calls. A loop that ends without `task_done` keeps its findings but records "loop terminated without task_done" in `metrics.reviewDepth`. Future backend tools (suppressions, previous-feedback, submit) attach through pydantic-ai's MCP capability (`MCPServerStreamableHTTP`) without new pipeline code.

`ExecuteReasoningEngineStage` selects `ctx.cfg.reasoning_engine`, stores `review-result.json`, projects the result to `final-findings.json`, and records metrics. `FAST_REVIEW` and `--fast-review` are compatibility aliases for `single_pi` only when no explicit reasoning engine is configured. The selected engine does not automatically fall back to another engine.

To add an engine, implement the abstract interface, register it during package initialization, add configuration acceptance, and test the observable result and selection path. See [adding an engine](../guides/adding-a-reasoning-engine.md).
