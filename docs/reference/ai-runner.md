# AI runner

## Purpose

Document `auto_pr_reviewer.ai` — the `PiRunner` subprocess wrapper, session reuse, JSON repair, and prompt assembly. This is the **explanation + reference** for how the package talks to the LLM.

## Audience

- Maintainers tuning AI cost (token usage, session reuse, prompt size).
- Maintainers debugging a Pi call (timeout, parse error, repair loop).

## The `pi` CLI

The reviewer shells out to the `pi` CLI (a separate tool) as a JSON-producing subprocess. We never call OpenAI / Anthropic APIs directly. The CLI is configured to:

- Be read-only (`--tools read,grep` — no `write`, `edit`, or `bash`).
- Skip extension / skills / prompt-template discovery (`--no-extensions --no-skills --no-prompt-templates`).
- Skip project-context file loading (`--no-context-files`).
- Run in non-interactive mode (`-p <instruction>`).
- Set a model pattern and a thinking level.
- Append a system prompt from a file (`--append-system-prompt <path>`).

These flags are hard-coded in `PiRunner._build_cmd` so the reviewer cannot accidentally enable write tools or external context.

## `PiRunner` lifecycle

```python
class PiRunner:
    def __init__(self, cfg: Config): ...
    @property
    def last_tokens(self) -> dict[str, int]: ...
    @property
    def session_id(self) -> str: ...
    def run_json(self, prompt_path, stdin_text, output_path, stage) -> None: ...
```

Construction is cheap — no I/O happens until `run_json` is called.

`run_json` is the only public method that launches the subprocess. It:

1. Builds the `pi` command (see below).
2. Builds the subprocess env: copy of `os.environ` with `ADO_AUTH_TOKEN` / `ADO_MCP_AUTH_TOKEN` / `ADO_API_KEY` stripped.
3. Calls `subprocess.run(cmd, input=stdin_text.encode(), stdout=PIPE, stderr=PIPE, timeout=cfg.pi_timeout_secs)`.
4. Logs the Pi call to stderr.
5. Parses `stderr` for token usage (regex on lines like `tokens: 100 in / 50 out`).
6. Writes the JSON output to `output_path`.
7. Strips Markdown code fences if the model wrapped the JSON in them.
8. Parses the JSON. If it fails, retries once with a "return only JSON" repair prompt in the same session.

The method raises `SystemExit` on timeout, non-zero exit, or unrecoverable JSON failure.

## Session reuse

The biggest cost saver in the package. By default, all stage calls in a single review run use the same `pi` session, so the model keeps the system prompt, the diff, and the prior turn's context between calls.

```python
# From _build_cmd:
cmd = [
    "pi",
    *(["--no-session"] if not self.cfg.pi_session_enabled else []),
    *(["--session-id", self.session_id] if self.cfg.pi_session_enabled else []),
    *(["--clear-session"] if self.cfg.pi_session_clear else []),
    ...
]
```

### Why `--session-id` and not `--session`?

Pi's CLI has two flags:

- `--session <path|id>` — open a *specific* session; error if it does not exist.
- `--session-id <id>` — open by exact project session id; **create if missing**.

We use `--session-id` because the first stage call of a fresh run does not have a session yet. `--session` would error out; `--session-id` creates one. Subsequent stage calls find the existing session and resume it.

### Default session id

`PiRunner.session_id` returns `cfg.pi_session_id` if set, otherwise `pr-<pr_id>-review-<run_id>`. The run id is what makes the session deterministic across reruns: re-running the same PR with the same `REVIEW_RUN_ID` resumes the same session.

### Disabling session reuse

Set `PI_SESSION_ENABLED=0` or pass `--no-pi-session`. Each stage gets a fresh `pi` call with no retained context. This is the deterministic mode for re-evaluating prompts — the model has no memory of prior turns.

### Clearing the session

`PI_SESSION_CLEAR=1` (or `--pi-session-clear`) starts a fresh session under the same id, ignoring any prior state. Useful after a schema change, a corrupted prior state, or when you want to benchmark prompts in a clean session without renaming.

## Token savings (Phase B)

When session reuse is enabled, the prompts intentionally shrink. From `ai/prompts.py`'s docstring:

> When `cfg.pi_session_enabled` is true, the model retains the full context from previous stages in its Pi session. So we shrink the per-stage user message: instead of re-embedding the metadata, work items, threads, and previous-stage JSON, we pass file paths and let the model's `read,grep` tools load them on demand.

In legacy / deterministic mode (`pi_session_enabled=False`), every payload is embedded verbatim. The pipeline still works, just more expensively.

The combination — session reuse on the `pi` side + minimal per-stage payloads on our side — is what keeps the token cost of a typical 11-stage review under 30k tokens, even for large diffs.

## JSON repair loop

The model occasionally produces JSON wrapped in Markdown fences (` ```json ... ``` `) or with stray prose. The runner handles these gracefully:

1. **First attempt** — parse the output as JSON. If it succeeds, return.
2. **Markdown fence strip** — `strip_json_fences` removes any line that starts with ```` ``` ````. Re-parse.
3. **Repair call** — if parsing still fails, build a new `pi` command (same session, no stdin payload) with the instruction "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose." Re-parse the result.
4. **Final failure** — `SystemExit` with a clear error message.

In session mode, the repair call is cheap: it sends empty stdin and the model already has the original context. In non-session mode, the repair call resends the full stdin payload — same cost as the original call.

## Prompt assembly

`ai/prompts.py` has three builders:

| Builder | Returns | Used by |
|---|---|---|
| `system_prompt(cfg)` | Combined system prompt (reviewer prompt + language hint + standards file) | every stage's `--append-system-prompt` |
| `*_payload(cfg, ...)` per stage | The user-message text sent on stdin | the corresponding stage |

The system prompt is built once per run and reused for every stage. The per-stage payloads shrink in session mode (paths only) and stay full in non-session mode (embedded data).

The system prompt path is `Config.review_prompt_path`, configurable per env. The per-stage prompts (`Intent` / `ContextPlan` / `ContextDigest` / `Review` / `Verify` / `Severity`) live alongside it.

## Token usage tracking

After every `pi` call, the runner parses stderr for token-usage lines:

```python
_TOKEN_RE = re.compile(
    r"tokens?[:\s]+(?P<in>\d+)\s*(?:in|input)?\s*[/,]\s*(?P<out>\d+)\s*(?:out|output)?",
    re.IGNORECASE,
)
```

The match is best-effort: if Pi's output format changes, the regex silently misses and the token count is empty. The runner stores the parsed dict in `self._last_tokens` and exposes it as `runner.last_tokens`.

Stages copy this into `ctx.last_token_usage` so the orchestrator can aggregate it into `RunSummary.stages[].token_usage` and into the top-level token total.

## Defense in depth: scrubbing the subprocess env

```python
def _scrub_ado_env(env: dict[str, str]) -> None:
    for key in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
        env.pop(key, None)
```

This is the only place in the package besides `Config` that reads the process env. The motivation: the model can see its own env (via `os.environ` if it spawns a subprocess, or via the prompt context if it's introspective). The ADO token must never reach the model — that would be a credential leak into the model's logs, training data, or output.

The scrub is conservative: it removes all known aliases. If a new alias is added to `Config._ENV_ALIASES["ado_token"]`, it must also be added here.

## Timeouts and retries

- `Config.pi_timeout_secs` (default 300s) bounds the subprocess call. On timeout, the runner raises `SystemExit` with a clear message. The orchestrator records this as a stage failure.
- There is **no** retry loop. A timeout means the model did not respond in time; re-running the same prompt is unlikely to help and would compound the cost. Operators should increase the timeout or split the prompt (e.g. enable chunked review).

## Common debugging questions

- **"Why is `last_tokens` empty?"** — Pi's stderr did not match the regex. Update the regex in `runner._parse_token_usage` or check that Pi is emitting token lines in the expected format.
- **"Why does session reuse not work?"** — Either `pi_session_enabled=False` (the env or flag), or the subprocess is running outside the container (the session file is stored under the project directory, which is mounted at the same path in the container).
- **"Why is the repair loop firing?"** — The model is producing invalid JSON. The repair call usually fixes it; if it doesn't, the runner raises `SystemExit` and the run fails. Inspect the raw output in the stage's artifact (`<artifact_dir>/raw/<stage>.txt` if you enabled raw capture, or re-run with `pi_session_enabled=False` to capture the exact prompt).

## Where to look next

- Schemas for the JSON outputs: [`pipeline.md`](pipeline.md#schemas)
- Artifact layout: [`artifacts.md`](artifacts.md)
- Run summary: [`artifacts.md`](artifacts.md#run-summary)
