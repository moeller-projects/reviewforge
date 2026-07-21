# Extension points

**Purpose:** identify supported seams for customization. **Audience:** maintainers and integrators. **Mode:** reference.

- **Reasoning engines:** subclass `ReasoningEngine`, implement `name` and `execute`, then call `register_engine`.
- **Pipeline stages:** subclass `Stage`, set `name`, implement `run`, and optionally override `should_run`; add the instance to an explicit pipeline list.
- **Prompt files:** configure prompt paths through `Config`; preserve the output schema and runtime composition contract.
- **ADO client boundary:** `AdoClient` exposes PR, threads, commits, thread creation, comments, voting, and generic GET/POST/PUT helpers.
- **Projection boundary:** `review_result_to_final_doc` is the compatibility seam from canonical rich output to posting JSON.

There is no plugin package, entry-point discovery mechanism, or dynamic extension configuration in the current implementation. New extension points should be added only with an explicit API contract and tests.
