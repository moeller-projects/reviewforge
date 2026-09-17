"""Native in-process reasoning engine (pydantic-ai + pydantic-ai-harness).

Replaces the Pi subprocess with an in-process agent loop:

- model: any pydantic-ai model string; default ``openai-codex:`` (ChatGPT
  subscription OAuth, no API key)
- tools: harness ``FileSystem(read_only=True)`` + :class:`ReviewCollectorToolset`
- output: ``ReviewNarrative`` (final) + collected findings → ``ReviewResult``

Findings are emitted as validated ``record_finding`` tool calls during the
loop, so a malformed finding is retried in isolation and a crashed loop
keeps already-recorded findings (observable on ``self.collector``).
"""
from __future__ import annotations

import json
import os
import time
from pathlib import Path
from typing import Any

from ..ai.prompts import _compose
from ..exceptions import ReasoningEngineError
from ..pipeline.schemas import (
    ModelMetadata,
    ReviewMetadata,
    ReviewNarrative,
    ReviewResult,
    TokenUsage,
)
from ..pipeline.stage import StageContext
from ..runlog import warning as log_warning
from .engine import ReasoningEngine, register_engine
from .native_tools import ReviewCollectorToolset
from .prefix import _build_single_pi_prefix
from .single_pi import _normalize_review

_NATIVE_INTRO = (
    "Native in-process reasoning review for Azure DevOps PR #{pr_id}.",
    "Report each confirmed issue with the record_finding tool, then call "
    "task_done and return only the ReviewNarrative JSON object defined in "
    "the system prompt.",
)


#: Read-denial patterns for the native loop's FileSystem capability.
#: Deny every secret-adjacent pattern: the harness default protected set
#: (``.git/``, ``.env``, ``.env.*``, ``*.pem``, ``*.key``, ``**/secrets*``)
#: plus direnv files; ``*.env`` is retained so non-dot env files
#: (``settings.env``) stay gated.
_NATIVE_DENIED_PATTERNS = [".git/*", "*.env", ".env.*", ".envrc", "*.pem", "*.key", "**/secrets*"]


class CodexFileCredentialSource:
    """``OpenAICodexCredentialSource`` backed by a Codex CLI ``auth.json`` file.

    ``load()`` reads the file; ``save()`` persists rotated tokens back with an
    atomic temp-write + rename so refresh-token rotation survives container
    restarts.
    """

    def __init__(self, path: Path) -> None:
        self._path = path

    async def load(self) -> Any:
        from pydantic_ai.exceptions import UserError
        from pydantic_ai.providers.openai_codex import OpenAICodexCredentials

        try:
            raw = self._path.read_text(encoding="utf-8")
        except OSError as exc:
            raise UserError(
                f"Cannot read Codex credentials at {self._path}: {exc}. "
                "Run `codex login` or check NATIVE_CREDENTIAL_PATH."
            ) from None
        try:
            data = json.loads(raw)
        except ValueError as exc:
            raise UserError(
                f"Malformed Codex credentials at {self._path}: {exc}. "
                "Run `codex login` or check NATIVE_CREDENTIAL_PATH."
            ) from None
        return OpenAICodexCredentials.from_codex_cli_auth(data)

    async def save(self, credentials: Any) -> None:
        existing: dict[str, Any] = {}
        try:
            loaded = json.loads(self._path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                existing = loaded
        except (OSError, ValueError):
            pass
        existing["tokens"] = {
            "access_token": credentials.access_token,
            "refresh_token": credentials.refresh_token,
            "account_id": credentials.account_id,
        }
        tmp = self._path.with_name(self._path.name + f".{os.getpid()}.tmp")
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                handle.write(json.dumps(existing, indent=2))
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise
        os.replace(tmp, self._path)


def resolve_native_model(cfg: Any) -> Any:
    """Resolve ``cfg.native_model`` into a pydantic-ai model.

    ``openai-codex:*`` builds a subscription-auth provider backed by the
    configured credential file; any other prefix is passed straight through
    for pydantic-ai to resolve (API-key env vars as usual).
    """
    model = cfg.native_model
    if model.startswith("openai-codex:"):
        from pydantic_ai.models.openai_codex import OpenAICodexModel
        from pydantic_ai.providers.openai_codex import OpenAICodexProvider

        source = CodexFileCredentialSource(Path(cfg.native_credential_path).expanduser())
        return OpenAICodexModel(
            model.split(":", 1)[1],
            provider=OpenAICodexProvider(credential_source=source),
        )
    return model


def resolve_thinking(raw: str | None) -> Any | None:
    """Map a normalized ``native_thinking`` config value to a ``ThinkingLevel``.

    Config normalizes to ``"true"``/``"false"`` or a literal level; this turns
    that back into the bool/literal pydantic-ai accepts. ``None`` means "leave
    the model at its provider default".
    """
    if raw is None:
        return None
    if raw == "true":
        return True
    if raw == "false":
        return False
    return raw


def _build_user_prompt(ctx: StageContext, diff_text: str) -> str:
    intro = (_NATIVE_INTRO[0].format(pr_id=ctx.cfg.pr_id), _NATIVE_INTRO[1])
    parts = [_build_single_pi_prefix(ctx, intro=intro)]
    if diff_text:
        parts += ["\nUnified diff:\n", diff_text]
    return "\n".join(parts) + (
        "\nReview every changed file with the read tools, report findings via "
        "record_finding, then call task_done and return only the ReviewNarrative "
        "JSON object defined in the system prompt.\n"
    )


class NativeReasoningEngine(ReasoningEngine):
    """In-process agent loop returning a full ``ReviewResult``."""

    def __init__(
        self,
        cfg: Any | None = None,
        *,
        model_override: Any | None = None,
        collector: ReviewCollectorToolset | None = None,
    ) -> None:
        self._cfg = cfg
        #: Test seam: replaces model resolution when set.
        self._model_override = model_override
        #: The collector used by the most recent run; findings recorded before
        #: a crash remain observable here.
        self.collector = collector

    @property
    def name(self) -> str:
        return "native"

    def _build_agent(
        self, cfg: Any, ctx: StageContext, collector: ReviewCollectorToolset
    ) -> Any:
        from pydantic_ai import Agent
        from pydantic_ai_harness.compaction import SlidingWindowCompaction
        from pydantic_ai_harness.filesystem import FileSystem

        repo_dir = getattr(ctx.state, "repo_dir", None) or cfg.clone_root
        thinking = resolve_thinking(cfg.native_thinking)
        return Agent(
            model=self._model_override or resolve_native_model(cfg),
            output_type=ReviewNarrative,
            instructions=_compose(cfg.native_review_prompt_path, cfg, include_standards=True),
            model_settings={"thinking": thinking} if thinking is not None else None,
            capabilities=[
                FileSystem(
                    root_dir=repo_dir,
                    read_only=True,
                    denied_patterns=_NATIVE_DENIED_PATTERNS,
                    max_read_lines=cfg.native_read_max_lines,
                ),
                SlidingWindowCompaction(max_tokens=cfg.native_max_context_tokens),
            ],
            toolsets=[collector],
            retries=2,
        )

    def execute(self, ctx: StageContext) -> ReviewResult:
        from pydantic_ai.usage import UsageLimits

        cfg = ctx.cfg
        diff_text = getattr(ctx.state, "diff_text", "") or (
            ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
        )
        collector = self.collector or ReviewCollectorToolset(
            ctx.extras.get("context_staging_dir"), diff_text
        )
        self.collector = collector
        started_at = time.time()
        reasoning_started = time.perf_counter()
        agent = self._build_agent(cfg, ctx, collector)
        try:
            run = agent.run_sync(
                _build_user_prompt(ctx, diff_text),
                usage_limits=UsageLimits(request_limit=cfg.native_max_turns),
            )
        except Exception as exc:
            raise ReasoningEngineError(
                f"native reasoning failed: {exc}",
                details={
                    "engine": self.name,
                    "findings_recorded": len(collector.findings),
                    "error": str(exc),
                },
            ) from exc
        reasoning_duration_ms = int((time.perf_counter() - reasoning_started) * 1000)
        narrative = run.output
        if not collector.finished_deliberately:
            log_warning(
                "native engine loop ended without task_done; "
                "keeping recorded findings and flagging review depth"
            )
        return self._assemble(ctx, cfg, narrative, collector, run, started_at, reasoning_duration_ms)

    def _assemble(
        self,
        ctx: StageContext,
        cfg: Any,
        narrative: ReviewNarrative,
        collector: ReviewCollectorToolset,
        run: Any,
        started_at: float,
        reasoning_duration_ms: int,
    ) -> ReviewResult:
        """Combine the narrative output and collected tool calls into a ``ReviewResult``."""
        result = ReviewResult(
            review_summary=narrative.review_summary,
            verification_summary=narrative.verification_summary,
            pr_summary=narrative.pr_summary,
            findings=collector.findings,
            good_practices=narrative.good_practices,
            uncertainties=collector.uncertainties,
        )
        result = _normalize_review(result, ctx)
        usage = run.usage
        tokens = {
            "in": int(usage.input_tokens or 0),
            "out": int(usage.output_tokens or 0),
            "total": int(usage.total_tokens or 0),
        }
        ctx.last_token_usage = tokens
        finished_at = time.time()
        result.metadata = ReviewMetadata(
            started_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(started_at)),
            finished_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(finished_at)),
            duration_ms=int((finished_at - started_at) * 1000),
            model=ModelMetadata(model=cfg.native_model, reasoning_engine=self.name),
            tokens=TokenUsage(input=tokens["in"], output=tokens["out"], total=tokens["total"]),
        )
        depth = "agentic tool loop"
        if not collector.finished_deliberately:
            depth += "; loop terminated without task_done"
        result.metrics = result.metrics.model_copy(
            update={
                "piInputTokens": tokens["in"],
                "piOutputTokens": tokens["out"],
                "piTotalTokens": tokens["total"],
                "invocationCount": int(usage.requests or 0),
                "wallClockDurationMs": int((finished_at - started_at) * 1000),
                "reasoningDurationMs": reasoning_duration_ms,
                "changedFilesReviewed": len(getattr(ctx.state, "files", []) or []),
                "reviewDepth": depth,
            }
        )
        return result


register_engine(NativeReasoningEngine().name, NativeReasoningEngine)

__all__ = ["CodexFileCredentialSource", "NativeReasoningEngine", "resolve_native_model", "resolve_thinking"]
