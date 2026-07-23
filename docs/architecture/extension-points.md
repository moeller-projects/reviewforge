# Extension points

**Purpose:** identify supported seams for customization. **Audience:** maintainers and integrators. **Mode:** reference.

- **Reasoning engines:** subclass `ReasoningEngine`, implement `name` and `execute`, then call `register_engine`.
- **Pipeline stages:** subclass `Stage`, set `name`, implement `run`, and optionally override `should_run`; add the instance to an explicit pipeline list.
- **Prompt files:** configure prompt paths through `Config`; preserve the output schema and runtime composition contract.
- **ADO operations boundary:** `AdoClient` exposes REST primitives; `ado.operations` composes the supported fetch-context and post-findings workflows for pipeline use. The legacy ADO CLI is a compatibility wrapper.
- **Model runner:** `ModelRunner` is the narrow execution contract. `create_model_runner(Config)` selects the configured backend; only `PiCliRunner` (`MODEL_BACKEND=pi`) ships. Backends must scrub ADO credentials and restrict model tools to read-only operations.
- **Projection boundary:** `review_result_to_final_doc` is the compatibility seam from canonical rich output to posting JSON.

There is no plugin package, entry-point discovery mechanism, or dynamic extension configuration in the current implementation. New extension points should be added only with an explicit API contract and tests.
