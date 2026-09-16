# Plan 07 — Native Engine (pydantic-ai + pydantic-ai-harness, OpenAI Subscription Auth)

**Repo:** `moeller-projects/reviewforge` · **Branch:** `feat/native-engine` · **Size:** L

## Context

ReviewForge's reasoning is abstracted behind `ReasoningEngine` (`src/reviewforge/reasoning/engine.py`, `register_engine`). The production default `single_pi` shells out to the Pi coding-agent CLI (`ai/runner.py`): Pi runs the tool-use loop, reads the checkout and `.reviewforge-context/`, returns one large `ReviewResult` JSON. This plan adds a third engine, `native`, that runs the loop **in-process** via [pydantic-ai](https://ai.pydantic.dev/) + [pydantic-ai-harness](https://github.com/pydantic/pydantic-ai-harness), and authenticates through the **OpenAI Codex/ChatGPT subscription** (`openai-codex:` provider, OAuth credentials from `codex login`) instead of an API key.

Why these libraries (decision already made, do not re-litigate):

- `ReviewResult` and all stage schemas are Pydantic v2 — pydantic-ai validates model output against them directly.
- pydantic-ai-harness ships the harness pieces as composable capabilities: `FileSystem(read_only=True)` (sandboxed read-only file tools with traversal rejection and protected patterns), `Compaction` (context compression), `ToolOutputLimits`, `SpendLimits`, `Subagents`.
- pydantic-ai ≥ 2.41 ships the `openai-codex` provider: same OAuth flow as the Codex CLI, reads `~/.codex/auth.json`, pluggable credential store (`OpenAICodexCredentialSource`), never falls back to `OPENAI_API_KEY`.
- MCP is a pydantic-ai core capability (`pydantic_ai.mcp.MCPServerStdio` / Streamable HTTP) — the future reviewforge backend's MCP tools plug in without new code.

## Goal

`REASONING_ENGINE=native` runs a full PR review in-process: read-only tools, findings emitted as validated tool calls, `ReviewResult` assembled and returned through the existing engine interface. Pipeline, validation, anchors, dedupe, posting untouched. Pi engines remain registered and selectable.

## Non-goals

- No removal of `single_pi`/`multi_stage` (bake-off first, removal is a later decision).
- No shell tool, no write tools — read-only by construction.
- No concurrent sub-agent fan-out (that's Plan 03 territory; `Subagents` capability is the future hook, noted only).
- No streaming UI. `native` is a batch engine.

## Design

```text
ExecuteReasoningEngineStage
        │  REASONING_ENGINE=native
        ▼
NativeEngine.run(ctx)  ── mirrors single_pi's ctx/artifact contract
        │
        ├─ model:     Agent(model=resolve_model(cfg))        # default openai-codex:*
        ├─ output:    output_type=ReviewNarrative            # slim narrative schema (new)
        ├─ capabilities=[
        │     FileSystem(root_dir=checkout, read_only=True), # harness, sandboxed
        │     ReviewToolset(...),                            # custom: read_context,
        │     │                                              #   record_finding, task_done
        │     SlidingWindowCompaction(...),                  # long-loop safety
        │   ]
        ├─ findings:  collected via record_finding tool calls (validated per call)
        └─ assembly:  narrative + collected findings + usage → ReviewResult
```

**Findings as tool calls, not one giant JSON** (OCR `code_comment` model): the model calls `record_finding` per issue during the loop and `task_done` when finished. Each call is validated against `RichFinding` immediately — a bad finding is retried in isolation instead of failing the whole run, and a crashed loop keeps already-recorded findings. The final structured output is only the *narrative* (summaries), which is small and safe to produce in one shot.

## Implementation steps

### 1. Dependencies (`pyproject.toml`, `uv.lock`, `Dockerfile`)

```toml
dependencies = [
    # ...existing...
    "pydantic-ai-slim[openai,mcp]>=2.41.0",
    "pydantic-ai-harness>=<latest>",   # pin at implementation time
]
```

Run `uv lock`. The Dockerfile's `uv export ... | uv pip install` picks both up automatically — verify the runtime stage still builds (`python -m reviewforge.ops build`). No npm/Pi changes in this plan.

### 2. Config (`src/reviewforge/config.py`)

| Field | Type | Default | Env alias |
|---|---|---|---|
| `native_model` | `str` | `"openai-codex:gpt-5.6-luna"` | `NATIVE_MODEL` — **verify the current model id at implementation time**; any pydantic-ai model string works (`openai:…`, `anthropic:…`, `azure:…`) |
| `native_credential_path` | `str` | `"~/.codex/auth.json"` | `NATIVE_CREDENTIAL_PATH` — container: `/app/codex-auth.json` |
| `native_max_turns` | `int` | `30` | `NATIVE_MAX_TURNS` |
| `native_max_context_tokens` | `int` | `150000` | `NATIVE_MAX_CONTEXT_TOKENS` (compaction trigger) |
| `native_read_max_lines` | `int` | `2000` | passthrough to `FileSystem.max_read_lines` |

Engine selection: find the existing engine-selection config (docs: "`single_pi` is the default engine; `multi_stage` is available for … explicit fallback selection" — locate the field/env, likely `REASONING_ENGINE`) and register `native` in the same mechanism. Document all knobs in `docs/reference/environment-variables.md`.

### 3. Narrative schema (`src/reviewforge/pipeline/schemas.py`)

```python
class ReviewNarrative(_Base):
    """Final structured output of the native engine's agent loop.

    Findings are NOT part of this schema — they arrive via record_finding
    tool calls during the loop. Keeping the final output small removes the
    giant-JSON failure mode the Pi engines needed repair calls for.
    """

    review_summary: ReviewSummary
    verification_summary: VerificationSummary = Field(
        default_factory=lambda: VerificationSummary(summary="Findings verified via read tools during the loop.")
    )
    pr_summary: PrSummary = Field(default_factory=PrSummary)
    good_practices: list[GoodPractice] = Field(default_factory=list)
```

Add to `__all__` and `docs/reference/schemas.md`.

### 4. Custom toolset (`src/reviewforge/reasoning/native_tools.py`, new file)

A leaf `AbstractToolset` (pydantic-ai primitive — same pattern the harness's own capabilities use; see `pydantic_ai_harness/filesystem/_toolset.py` for the reference implementation style):

```python
"""Review-specific tools for the native engine.

Findings arrive as validated tool calls (OCR code_comment model), not as
one giant JSON document: per-call validation, partial results survive a
crashed loop, and task_done is the deliberate-clean signal.
"""
from __future__ import annotations

from pydantic_ai.toolsets import AbstractToolset
from pydantic_ai.tools import ToolDefinition
from pydantic import ValidationError

from ..pipeline.schemas import RichFinding, Uncertainty


class ReviewCollectorToolset(AbstractToolset):
    """Tools: read_context, record_finding, record_uncertainty, task_done.

    File reading itself comes from the harness FileSystem capability
    (read_only=True) — this toolset owns only review-domain tools.
    Collected state lives on the instance; the engine reads it after the run.
    """

    def __init__(self, context_dir: Path | None, diff_text: str):
        self._context_dir = context_dir      # staged .reviewforge-context/ dir
        self._diff_text = diff_text          # for anchor pre-validation (optional)
        self.findings: list[RichFinding] = []
        self.uncertainties: list[Uncertainty] = []
        self.finished_deliberately: bool = False   # set by task_done
```

Tools (each a method exposed via the toolset's `get_tools()`; follow the harness toolset pattern — `ToolDefinition` + `call_tool` dispatch):

1. **`read_context(filename: str) -> str`** — read one file from the staged `.reviewforge-context/` directory (the same files `single_pi`'s prefix advertises). Reject names containing `/`, `..`, or not present in the staging index. Byte-cap the result (reuse the `_byte_cap_with_pointer` helper location chosen in Plan 01).
2. **`record_finding(...)`** — parameters mirror `RichFinding` fields (`title, observation, impact, recommendation, severity, confidence?, file?, line?, contextBasis?, regression?, evidence`). Handler:
   - Validate with `RichFinding.model_validate(args)`.
   - On `ValidationError`: **return the error text as the tool result** (pydantic-ai `ModelRetry` semantics) so the model fixes the arguments — never raise out of the loop.
   - If Plan 02 (`existingCode` + resolver) has landed: attempt snippet resolution here; on failure append a hint to the tool result ("snippet not found verbatim in diff — re-anchor or withdraw") but still record (the stage-level anchor validation remains the authority).
   - Append to `self.findings`; return `"recorded (finding #N)"`.
3. **`record_uncertainty(topic, reason?, confidence?)`** — validated `Uncertainty`, appended.
4. **`task_done(summary: str = "") -> str`** — sets `finished_deliberately = True`; returns instruction to produce the final narrative. The engine treats loop end **without** `task_done` as `review_item_failed` semantics (transcript Plan 04): findings are kept, but `metrics.reviewDepth` notes "loop terminated without task_done" and a transcript `review_item_failed` event is emitted.

### 5. Engine (`src/reviewforge/reasoning/native.py`, new file)

Mirror `single_pi.py`'s public contract (read `reasoning/engine.py` and `single_pi.py` first — the engine must accept the same `StageContext`, read the same artifacts, return `ReviewResult`):

```python
"""Native in-process reasoning engine (pydantic-ai + pydantic-ai-harness).

Replaces the Pi subprocess with an in-process agent loop:
- model: any pydantic-ai model string; default openai-codex (ChatGPT
  subscription OAuth, no API key)
- tools: harness FileSystem(read_only=True) + ReviewCollectorToolset
- output: ReviewNarrative (final) + collected findings → ReviewResult
"""
```

Flow:

1. **Model resolution** — `resolve_native_model(cfg)`:
   - `native_model` starts with `openai-codex:` → construct the provider with an `OpenAICodexCredentialSource` whose `load()` reads `cfg.native_credential_path` and whose `save()` writes it back atomically (write temp + rename). Check the exact class names/signatures in the installed `pydantic_ai` version (`pydantic_ai.models.openai` / provider docs) — the provider landed in v2.41 and its API may differ in detail.
   - Any other prefix (`openai:`, `anthropic:`, `azure:`, …) → pass the string straight to `Agent(model=...)`; pydantic-ai resolves it (API-key env vars as usual). This keeps the API-key fallback one config change away.
2. **Prompt assembly** — reuse, don't fork: extract `_build_single_pi_prefix` from `single_pi.py` into `reasoning/prefix.py` (both engines import it; mechanical move, no behavior change). New system prompt `prompts/native-review-system.md` (step 6).
3. **Agent construction**:

   ```python
   agent = Agent(
       model=resolve_native_model(cfg),
       output_type=ReviewNarrative,
       instructions=rendered_system_prompt,
       capabilities=[
           FileSystem(
               root_dir=ctx.state.repo_dir,
               read_only=True,
               denied_patterns=[".git/*", "*.env", "**/secrets*"],
               max_read_lines=cfg.native_read_max_lines,
           ),
           collector,                      # ReviewCollectorToolset instance
           SlidingWindowCompaction(        # verify exact class name in installed version
               trigger_tokens=cfg.native_max_context_tokens,
           ),
       ],
       retries=2,
   )
   ```

   If `FileSystem`'s `read_file` lacks line-range parameters in the installed version, add a `read_file_range(path, start, end)` tool to `ReviewCollectorToolset` instead of fighting the capability.
4. **Run** — `result = agent.run_sync(user_prompt, usage_limits=UsageLimits(request_limit=cfg.native_max_turns))` (verify `UsageLimits` import path in installed version). Wrap in the engine's timing; catch provider/loop exceptions → `ReasoningEngineError` with the engine name, matching `single_pi` error semantics.
5. **Assembly** — build `ReviewResult`:
   - `review_summary`, `verification_summary`, `pr_summary`, `good_practices` from `result.output` (the `ReviewNarrative`).
   - `findings` = `collector.findings`; `uncertainties` = `collector.uncertainties`.
   - `metadata.model.model` = resolved model string; `metadata.model.reasoning_engine = "native"`.
   - `metadata.tokens` from `result.usage()` (map `input_tokens`/`output_tokens`/`total_tokens`; verify field names in installed version).
   - `metrics.invocationCount` from usage request count; `metrics.reviewDepth` notes the missing-`task_done` case (step 4.4).
   - `discarded_findings` = `[]` (anchor/filter stages own discards downstream).
6. **Artifacts** — write `ctx.artifacts.review_result` exactly as `single_pi` does; if transcript (Plan 04) exists, emit `llm_request`/`llm_response` per agent run (one logical run = one pair; per-round detail is inside pydantic-ai — out of scope for v1) and `review_item_done`/`review_item_failed` per the `task_done` rule.
7. **Register** — `register_engine("native", NativeEngine)` following the existing registration pattern in `reasoning/engine.py` / `reasoning/__init__.py`.

### 6. System prompt (`prompts/native-review-system.md`, new file)

Start from `prompts/fast-review-system.md` (same review doctrine, severity definitions, evidence requirements) and replace the output contract section:

```markdown
## How to report

- Read code with the file tools before judging. Comments must target the
  changed files; context reads are for background only.
- For EACH confirmed issue, call `record_finding` with complete fields and
  evidence. Never batch findings into prose.
- For each area you could not verify, call `record_uncertainty`.
- When every changed file has had its own pass, call `task_done`, then
  produce the final narrative JSON (review_summary, pr_summary,
  good_practices). Do not repeat findings in the narrative.
- If `record_finding` returns a validation error, fix the arguments and
  call again — do not work around it.
```

Keep the `existingCode` requirement if Plan 02 has landed.

### 7. Container + subscription auth wiring

1. Local: `codex login` once → `~/.codex/auth.json`.
2. `run.ps1` / `reviewforge/ops.py`: when `REASONING_ENGINE=native` and model is `openai-codex:*`, mount the credential file into the container (`-v ${NATIVE_CREDENTIAL_PATH}:/app/codex-auth.json:rw`) and set `NATIVE_CREDENTIAL_PATH=/app/codex-auth.json`. Mirror how `OPENAI_API_KEY` is propagated today.
3. **Refresh-token rotation**: tokens are single-use; the credential source's `save()` must persist every refresh or the next container run fails. Atomic write (temp + rename).
4. **Parallel runs**: `run-open-prs` spawns concurrent containers — sharing one credential file races (`refresh_token_reused`). v1: document that native+codex runs must be serialized (`-MaxPullRequests 1` style) or each run gets its own credential copy (`cp auth.json auth-$PR_ID.json` per container). A backend-side token broker is the v2 answer (belongs to the backend project, not this plan).
5. If auth fails, the error must say "run `codex login` or check NATIVE_CREDENTIAL_PATH" — actionable, not a raw 401.

### 8. MCP hook (documented, not wired)

Future backend tools (suppressions, previous-feedback, submit) attach as:

```python
from pydantic_ai.mcp import MCPServerStreamableHTTP   # verify import in installed version

capabilities.append(MCPServerStreamableHTTP(
    url="https://reviewforge-backend.example/mcp",
    headers={"Authorization": f"Bearer {token}"},
))
```

One-line note in `docs/architecture/reasoning-engine.md` that this is the intended backend integration point. Do not implement the backend here.

## Tests (`tests/test_native_engine.py`, new)

Use pydantic-ai's test utilities (`TestModel`/`FunctionModel` — verify names in installed version) to script tool calls without network:

1. `test_happy_path` — model calls `record_finding` twice + `task_done` → `ReviewResult` has 2 findings, narrative fields populated, engine name `native`.
2. `test_invalid_finding_retried` — first `record_finding` args violate schema → tool returns validation error → model retries with fixed args → finding recorded; run succeeds.
3. `test_missing_task_done_flagged` — loop ends via final output without `task_done` → findings kept, `reviewDepth`/transcript marks non-deliberate finish.
4. `test_read_only_toolset` — exposed tool names contain no write/edit/shell; `FileSystem(read_only=True)` filter verified.
5. `test_context_tool_reads_staging` — `read_context("crg-analysis.json")` returns staged content; `../etc/passwd` rejected.
6. `test_usage_mapped_to_metrics` — tokens and invocation count land in `ReviewMetadata`/`ReviewMetrics`.
7. `test_engine_registered` — engine registry resolves `native`; `single_pi`/`multi_stage` unaffected.
8. `test_model_resolution` — `openai-codex:*` builds credential-source-backed provider (mock source); `openai:*` passes through.
9. `test_credential_save_atomic` — credential source `save()` writes temp+rename; corrupt load → actionable error message.
10. `test_prefix_parity` — extracted `reasoning/prefix.py` produces byte-identical prefix for `single_pi` (regression guard for the refactor).
11. `test_crash_keeps_partial_findings` — model records 1 finding then errors → engine raises `ReasoningEngineError`, but the partial finding list is observable on the collector (assert the design decision explicitly).

## Docs

- `docs/architecture/reasoning-engine.md`: `native` engine section — loop, toolset, collector pattern, task_done semantics, auth.
- `docs/reference/environment-variables.md`: five new variables.
- `docs/reference/schemas.md`: `ReviewNarrative`.
- `docs/guides/running-reviews.md`: `REASONING_ENGINE=native` quickstart incl. `codex login`.
- `docs/guides/operations.md`: container credential mounting + serialization caveat for parallel runs.
- `CHANGELOG.md`; OpenSpec scaffold per repo convention.

## Verification

```bash
python -m pytest tests/test_native_engine.py -v
python -m pytest
codex login
REASONING_ENGINE=native reviewforge review --dry-run     # real PR, subscription auth
REASONING_ENGINE=single_pi reviewforge review --dry-run  # same PR, bake-off diff
```

## Acceptance criteria

1. `REASONING_ENGINE=native` completes a full review with zero Pi involvement and no `OPENAI_API_KEY` — auth via Codex subscription credentials only.
2. Every finding is schema-validated at emission time; a malformed finding never fails the run.
3. No write/edit/shell tool exists in the loop (asserted in tests).
4. `single_pi` behavior byte-identical after the prefix extraction refactor.
5. Refreshed OAuth tokens persist across container runs; parallel-run race is documented with the serialization workaround.
6. Bake-off: on 5+ real PRs, `native` findings overlap `single_pi` findings by fingerprint at a rate you record in the PR description (target ≥70% before considering default switch — the number is yours to set, the measurement is not optional).
