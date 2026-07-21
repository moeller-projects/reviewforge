# AI interaction

**Purpose:** describe how Pi is invoked safely. **Audience:** maintainers changing prompts or model execution. **Mode:** explanation.

`ai.runner.PiRunner` invokes the external `pi` CLI as a JSON producer. It composes prompt files through `ai.prompts`, records token usage from stderr, strips ADO token variables from the child environment, and repairs invalid JSON in the same session with a JSON-only instruction.

Session reuse is enabled by default. The default identifier is `pr-<pr_id>-review`; `--pi-session-id` overrides it, `--no-pi-session` disables reuse, and `--pi-session-clear` starts fresh state under the same id. The session behavior matters most to the multi-stage engine and chunked calls.

Prompts are files, not embedded Python templates. Runtime augmentation adds language and standards where applicable. See [prompt reference](../reference/prompts.md) and [prompt development](../guides/prompt-development.md).

The model response is parsed and validated against Pydantic schemas. Invalid output is not silently coerced into a valid finding; schema errors are surfaced through the domain error path.
