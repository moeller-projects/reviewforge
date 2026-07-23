"""Single Pi reasoning engine.

Performs the entire review in one logical reasoning invocation. Python
reduces oversized diff context before calling Pi; optional JSON formatting
repair is tracked by ``PiRunner``. The model may read nearby files through
read-only tools and returns a structured ``ReviewResult``. Compatibility
artifacts are synthesized from the result.
"""
from __future__ import annotations

import json
import time
from typing import Any

from ..artifacts.builder import read_json, write_json
from ..exceptions import ReasoningEngineError, SchemaValidationError
from ..pipeline.projection import review_result_to_final_doc
from ..pipeline.schemas import ReviewResult
from ..pipeline.stage import StageContext
from .engine import ReasoningEngine, register_engine


def _runner_usage(runner: Any) -> dict[str, int]:
    usage = getattr(runner, "token_usage", None)
    if isinstance(usage, dict):
        return {k: int(usage.get(k, 0) or 0) for k in ("in", "out", "total")}
    usage = getattr(runner, "last_tokens", {})
    return usage if isinstance(usage, dict) else {}


def _runner_count(runner: Any, name: str) -> int:
    value = getattr(runner, name, 0)
    return value if isinstance(value, int) else 0


def _reduce_diff(diff_text: str, max_bytes: int) -> tuple[str, bool]:
    """Deterministically keep changed hunks within ``max_bytes``.

    Each file receives an equal share of the byte budget. Changed lines are
    preferred over ``diff --git`` metadata; headers are included whenever the
    share can accommodate them. A final UTF-8-safe prefix is a defensive cap.
    """
    if max_bytes <= 0:
        return "", bool(diff_text)
    encoded = diff_text.encode("utf-8")
    if len(encoded) <= max_bytes:
        return diff_text, False

    sections = [section for section in diff_text.split("diff --git ") if section]
    if not sections:
        return encoded[:max_bytes].decode("utf-8", "ignore"), True

    pieces: list[str] = []
    remaining = max_bytes
    for index, section in enumerate(sections):
        lines = section.splitlines()
        if not lines:
            continue
        header = f"diff --git {lines[0]}"
        changed_lines = [
            line for line in lines[1:]
            if (line.startswith("+") or line.startswith("-"))
            and not line.startswith(("+++", "---"))
        ]
        body = "\n".join(changed_lines) or "\n".join(lines[1:])
        sections_left = len(sections) - index
        share = remaining // sections_left
        header_bytes = len(header.encode("utf-8")) + 1
        if body and share > header_bytes:
            piece = header + "\n" + body.encode("utf-8")[: share - header_bytes].decode("utf-8", "ignore")
        else:
            piece = _utf8_prefix(body or header, share)
        if piece:
            pieces.append(piece.rstrip())
            used = len(piece.encode("utf-8"))
            remaining = max(0, remaining - used - 1)

    result = _utf8_prefix("\n".join(pieces), max_bytes)
    return result, True


def _utf8_prefix(text: str, max_bytes: int) -> str:
    """Return the longest UTF-8-safe prefix fitting ``max_bytes``."""
    return text.encode("utf-8")[:max_bytes].decode("utf-8", "ignore")

def _build_single_pi_instruction(ctx: StageContext) -> str:
    """Build the user message for the single-call reasoning engine."""
    metadata = ctx.metadata if ctx.metadata else read_json(ctx.artifacts.metadata) or {}
    files_text = getattr(ctx, "files_text", "") or "\n".join(
        getattr(ctx.state, "files", []) if ctx.state is not None else []
    ) or "(no changed files)"
    wi = ctx.extras.get("wi_context", [])
    threads = ctx.extras.get("thread_context", [])

    diff_text = ""
    if ctx.state is not None and ctx.state.diff_text:
        diff_text = ctx.state.diff_text
    elif ctx.artifacts.diff.exists():
        diff_text = ctx.artifacts.diff.read_text(encoding="utf-8")
    diff_text, reduced = _reduce_diff(diff_text, getattr(ctx.cfg, "max_diff_bytes", 0))

    parts = [
        f"Single-call reasoning review for Azure DevOps PR #{ctx.cfg.pr_id}.",
        "Return only the rich ReviewResult JSON object defined in the system prompt.",
    ]
    if reduced:
        parts.append(
            "\nThe unified diff was deterministically reduced to fit the context budget. "
            "Use changed-file headers and read-only tools before reporting uncertainty."
        )
    if metadata:
        parts += ["\nRepository/project metadata:", json.dumps(metadata, ensure_ascii=False)]
    parts += ["\nChanged files:", files_text]
    if wi:
        parts += [f"\nLinked work items:\n{json.dumps(wi, ensure_ascii=False)}"]
    if threads:
        parts += [f"\nExisting PR comments:\n{json.dumps(threads, ensure_ascii=False)}"]
    review_context = ctx.extras.get("review_context")
    if review_context:
        parts += [
            "\nDeterministic review state:\n"
            + json.dumps(review_context, ensure_ascii=False, sort_keys=True)
        ]
    if diff_text:
        parts += ["\nUnified diff:\n", diff_text]
    return "\n".join(parts) + "\nReturn only the ReviewResult JSON object defined in the system prompt.\n"


class SinglePiReasoningEngine(ReasoningEngine):
    """One Pi call that returns a full ``ReviewResult``."""

    def __init__(self, cfg: Any | None = None) -> None:
        self._cfg = cfg

    @property
    def name(self) -> str:
        return "single_pi"

    def execute(self, ctx: StageContext) -> ReviewResult:
        cfg = ctx.cfg
        instruction = _build_single_pi_instruction(ctx)
        output_path = ctx.artifacts.raw_dir / "fast-review.json"

        started_at = time.time()
        reasoning_started = time.perf_counter()
        ctx.pi.run_json(cfg.fast_review_prompt_path, instruction, output_path, "single-pi reasoning")
        reasoning_duration_ms = int((time.perf_counter() - reasoning_started) * 1000)
        tokens = _runner_usage(ctx.pi)
        ctx.last_token_usage = tokens

        raw = read_json(output_path)
        if raw is None:
            raise ReasoningEngineError(
                "single-pi reasoning produced no JSON",
                details={"output_path": str(output_path)},
            )

        validation_started = time.perf_counter()
        try:
            result = ReviewResult.model_validate(raw)
        except Exception as exc:
            raise SchemaValidationError(
                "single-pi response does not match ReviewResult schema",
                details={"error": str(exc), "output_path": str(output_path)},
            ) from exc
        validation_duration_ms = int((time.perf_counter() - validation_started) * 1000)

        projection_duration_ms = 0
        finished_at = time.time()
        result = self._enrich_metadata(result, cfg, started_at, finished_at, tokens)
        result.metrics = result.metrics.model_copy(
            update={
                "piInputTokens": tokens.get("in", 0),
                "piOutputTokens": tokens.get("out", 0),
                "piTotalTokens": tokens.get("total", 0),
                "invocationCount": _runner_count(ctx.pi, "invocation_count"),
                "repairInvocationCount": _runner_count(ctx.pi, "repair_invocation_count"),
                "wallClockDurationMs": int((finished_at - started_at) * 1000),
                "reasoningDurationMs": reasoning_duration_ms,
                "projectionDurationMs": projection_duration_ms,
                "validationDurationMs": validation_duration_ms,
                "changedFilesReviewed": len(getattr(ctx.state, "files", [])),
            }
        )
        write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))
        write_json(ctx.artifacts.final, review_result_to_final_doc(result))
        return result

    def _enrich_metadata(
        self,
        result: ReviewResult,
        cfg: Any,
        started_at: float,
        finished_at: float,
        tokens: dict[str, int] | None,
    ) -> ReviewResult:
        """Fill in run metadata that the model cannot know."""
        from ..pipeline.schemas import ModelMetadata, ReviewMetadata, TokenUsage

        tokens = tokens or {}

        result.metadata = ReviewMetadata(
            started_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(started_at)),
            finished_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(finished_at)),
            duration_ms=int((finished_at - started_at) * 1000),
            model=ModelMetadata(
                model=cfg.pi_model,
                reasoning_engine=self.name,
            ),
            tokens=TokenUsage(
                input=int(tokens.get("in", 0) or 0),
                output=int(tokens.get("out", 0) or 0),
                total=int(tokens.get("total", 0) or 0),
            ),
        )
        return result



register_engine(SinglePiReasoningEngine().name, SinglePiReasoningEngine)
