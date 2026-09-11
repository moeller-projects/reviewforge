# AI interaction

**Purpose:** describe how Pi is invoked safely. **Audience:** maintainers changing prompts or model execution. **Mode:** explanation.

`ai.model_runner.ModelRunner` is the model execution contract used by engines and stages. It exposes JSON execution plus token and invocation counters; every backend must scrub ADO credentials from child environments and restrict model-side tools to read-only operations.

`create_model_runner(Config)` currently supports only `MODEL_BACKEND=pi`, creating `ai.runner.PiCliRunner`. `PiRunner` remains a deprecated compatibility alias for one release. Pi composes prompt files through `ai.prompts`, records token usage from stderr, and repairs invalid JSON in the same session with a JSON-only instruction.

Session reuse is enabled by default for the Pi backend. The default identifier is `pr-<pr_id>-review`; `--pi-session-id` overrides it, `--no-pi-session` disables reuse, and `--pi-session-clear` starts fresh state under the same id. The session behavior matters most to the multi-stage engine and chunked calls.

Prompts are files, not embedded Python templates. Runtime augmentation appends the review language to every prompt and the coding standards to the fast-review and legacy review prompts only. See [prompt reference](../reference/prompts.md) and [prompt development](../guides/prompt-development.md).

When `CRG_ENABLED` is set, `EnrichWithCrgStage` prepends deterministic graph context before the diff and refreshes the complete graph artifact in `.reviewforge-context/`. The section is produced Python-side by the `code-review-graph` package — no model call, no MCP server, no extra Pi tools — and is capped and ordered. CRG failure still degrades the graph section without failing the review; the context-file preamble may remain when repository staging succeeded.

## Deterministic context files

After repository preparation, ReviewForge copies the complete available context into `.reviewforge-context/` under the disposable checkout. The directory contains an `index.json` plus redacted copies of metadata, commits, `changed-files.json`, work items, threads, review state, and current graph context when those artifacts exist. It is skipped when the checkout already owns that path, refreshed after current CRG enrichment, and removed with the checkout; it is not part of `ARTIFACT_NAMES`.

`single_pi` keeps a curated summary inline. When a list or byte budget is truncated, the summary ends with a pointer such as `.reviewforge-context/graph-context.json (key: impacted_files)`. Pointers are omitted when staging is unavailable, so prompts never contain dangling paths. Chunked reviews put the preamble and pointers only in chunk 1; later chunks carry their diff only. Pi runs with the checkout as its explicit cwd, and the runner records best-effort reads of `.reviewforge-context/` in `context_file_reads`; unparsable Pi stderr reports `unknown`.

The model response is parsed and validated against Pydantic schemas. Invalid output is not silently coerced into a valid finding; schema errors are surfaced through the domain error path.
