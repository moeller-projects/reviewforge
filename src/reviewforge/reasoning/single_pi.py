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
from ..git import ops as git_ops
from ..ado.posting import _normalize_title
from ..pipeline.projection import review_result_to_final_doc
from ..pipeline.schemas import ChunkResult, ReviewResult, TokenUsage
from ..pipeline.stage import StageContext
from .engine import ReasoningEngine, register_engine


def _runner_usage(runner: Any) -> dict[str, int]:
    usage = getattr(runner, "token_usage", None)
    if isinstance(usage, dict):
        return {k: int(usage.get(k, 0) or 0) for k in ("in", "out", "total")}
    usage = getattr(runner, "last_tokens", {})
    if isinstance(usage, dict):
        return {k: int(usage.get(k, 0) or 0) for k in ("in", "out", "total")}
    return {}


def _runner_count(runner: Any, name: str) -> int:
    value = getattr(runner, name, 0)
    return value if isinstance(value, int) else 0


def _build_single_pi_prefix(ctx: StageContext) -> str:
    """Build the shared non-diff prefix for single-pi prompts."""
    metadata = ctx.metadata or (
        read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {}
    )
    files_text = getattr(ctx, "files_text", "") or "\n".join(
        getattr(ctx.state, "files", []) if ctx.state is not None else []
    ) or "(no changed files)"
    parts = [
        f"Single-call reasoning review for Azure DevOps PR #{ctx.cfg.pr_id}.",
        "Return only the rich ReviewResult JSON object defined in the system prompt.",
    ]
    if metadata:
        parts += ["\nRepository/project metadata:", json.dumps(metadata, ensure_ascii=False)]
    parts += ["\nChanged files:", files_text]
    commits = _commit_lines(ctx)
    if commits:
        parts += ["\nCommits in this PR:", "\n".join(commits)]
    for label, value in (("Linked work items", ctx.extras.get("wi_context", [])), ("Existing PR comments", ctx.extras.get("thread_context", []))):
        if value:
            parts += [f"\n{label}:\n{json.dumps(value, ensure_ascii=False)}"]
    if review_context := ctx.extras.get("review_context"):
        parts += ["\nDeterministic review state:\n" + json.dumps(review_context, ensure_ascii=False, sort_keys=True)]
        feedback = review_context.get("previousFeedback", [])
        if feedback:
            parts += [
                "\nPrevious review feedback:\n",
                json.dumps(feedback, ensure_ascii=False, sort_keys=True),
                "\nDo not re-raise dismissed findings unless the implicated code changed in THIS diff. "
                "Treat fixed findings as addressed, but flag them when reintroduced and set regression=true.",
            ]
    return "\n".join(parts)


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

def _commit_lines(ctx: StageContext) -> list[str]:
    if ctx.artifacts.commits.exists():
        text = ctx.artifacts.commits.read_text(encoding="utf-8")
    elif ctx.state is not None and getattr(ctx.state, "repo_dir", None):
        text = git_ops.run_git(ctx.state.repo_dir, "log", "--oneline", ctx.state.range_spec)
    else:
        text = ""
    return text.splitlines()[:getattr(ctx.cfg, "commit_context_max", 50)]


def _diff_chunks(diff_text: str, max_bytes: int) -> list[str]:
    """Partition a unified diff at file boundaries in stable source order."""
    if len(diff_text.encode("utf-8")) <= max_bytes:
        return [diff_text]
    sections = [f"diff --git {part}" for part in diff_text.split("diff --git ") if part]
    chunks: list[str] = []
    current = ""
    for section in sections:
        if current and len((current + section).encode("utf-8")) > max_bytes:
            chunks.append(current)
            current = ""
        current += section
    if current:
        chunks.append(current)
    return chunks


def _build_single_pi_instruction(ctx: StageContext) -> str:
    """Build the user message for a non-chunked reasoning review."""
    prefix = _build_single_pi_prefix(ctx)
    diff_text = getattr(ctx.state, "diff_text", "") or (
        ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
    )
    parts = [prefix]
    if diff_text:
        parts += ["\nUnified diff:\n", diff_text]
    return "\n".join(parts) + "\nReturn only the ReviewResult JSON object defined in the system prompt.\n"


def _build_chunk_instruction(
    ctx: StageContext,
    chunk: str,
    index: int,
    total: int,
    *,
    include_shared_prefix: bool,
) -> str:
    prefix = _build_single_pi_prefix(ctx) if include_shared_prefix or index == 1 else ""
    body = (
        f"Review chunk {index}/{total} of the same PR diff. "
        "Return only a JSON object with findings and uncertainties; do not summarize the PR.\n"
        f"Unified diff chunk:\n{chunk}"
    )
    return f"{prefix}\n\n{body}" if prefix else body


class SinglePiReasoningEngine(ReasoningEngine):
    """One Pi call that returns a full ``ReviewResult``."""

    def __init__(self, cfg: Any | None = None) -> None:
        self._cfg = cfg

    @property
    def name(self) -> str:
        return "single_pi"

    def execute(self, ctx: StageContext) -> ReviewResult:
        cfg = ctx.cfg
        diff_text = getattr(ctx.state, "diff_text", "") or (
            ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
        )
        chunks = _diff_chunks(diff_text, cfg.max_diff_bytes)
        started_at = time.time()
        reasoning_started = time.perf_counter()
        chunk_usage: list[TokenUsage] = []

        if len(chunks) == 1:
            output_path = ctx.artifacts.raw_dir / "fast-review.json"
            ctx.pi.run_json(cfg.fast_review_prompt_path, _build_single_pi_instruction(ctx), output_path, "single-pi reasoning")
            raw = read_json(output_path)
            if raw is None:
                raise ReasoningEngineError("single-pi reasoning produced no JSON", details={"output_path": str(output_path)})
            try:
                result = ReviewResult.model_validate(raw)
            except Exception as exc:
                raise SchemaValidationError("single-pi response does not match ReviewResult schema", details={"error": str(exc), "output_path": str(output_path)}) from exc
        else:
            findings = []
            uncertainties = []
            seen: set[tuple[str | None, int | None, str]] = set()
            previous_tokens = _runner_usage(ctx.pi)
            repeat_shared_prefix = not cfg.pi_session_enabled or cfg.pi_session_clear
            for index, chunk in enumerate(chunks, 1):
                output_path = ctx.artifacts.raw_dir / f"fast-review-{index}.json"
                ctx.pi.run_json(
                    cfg.fast_review_prompt_path,
                    _build_chunk_instruction(
                        ctx,
                        chunk,
                        index,
                        len(chunks),
                        include_shared_prefix=repeat_shared_prefix,
                    ),
                    output_path,
                    f"single-pi chunk {index}/{len(chunks)}",
                )
                raw = read_json(output_path)
                try:
                    partial = ChunkResult.model_validate(raw)
                except Exception as exc:
                    raise SchemaValidationError(
                        "single-pi chunk response does not match ChunkResult schema",
                        details={"error": str(exc), "output_path": str(output_path)},
                    ) from exc
                current_tokens = _runner_usage(ctx.pi)
                chunk_usage.append(
                    TokenUsage(
                        input=max(0, current_tokens.get("in", 0) - previous_tokens.get("in", 0)),
                        output=max(0, current_tokens.get("out", 0) - previous_tokens.get("out", 0)),
                        total=max(0, current_tokens.get("total", 0) - previous_tokens.get("total", 0)),
                    )
                )
                previous_tokens = current_tokens
                for finding in partial.findings:
                    key = (finding.file, finding.line, _normalize_title(finding.title))
                    if key not in seen:
                        seen.add(key)
                        findings.append(finding.model_dump(by_alias=True))
                uncertainties.extend(item.model_dump(by_alias=True) for item in partial.uncertainties)
            result = ReviewResult.model_validate({
                "review_summary": {"summary": f"Reviewed {len(chunks)} coherent diff chunks."},
                "verification_summary": {"summary": "Reviewed each deterministic unified-diff chunk.", "approach": "chunked diff review"},
                "pr_summary": {"implementation_summary": f"Reviewed {len(chunks)} unified-diff chunks."},
                "findings": findings,
                "uncertainties": uncertainties,
            })

        reasoning_duration_ms = int((time.perf_counter() - reasoning_started) * 1000)
        tokens = _runner_usage(ctx.pi)
        ctx.last_token_usage = tokens
        finished_at = time.time()
        result = self._enrich_metadata(result, cfg, started_at, finished_at, tokens)
        result.metrics = result.metrics.model_copy(update={
            "piInputTokens": tokens.get("in", 0), "piOutputTokens": tokens.get("out", 0),
            "piTotalTokens": tokens.get("total", 0), "invocationCount": _runner_count(ctx.pi, "invocation_count"),
            "repairInvocationCount": _runner_count(ctx.pi, "repair_invocation_count"),
            "wallClockDurationMs": int((finished_at - started_at) * 1000),
            "reasoningDurationMs": reasoning_duration_ms, "projectionDurationMs": 0,
            "validationDurationMs": 0, "changedFilesReviewed": len(getattr(ctx.state, "files", [])),
            "chunkCount": len(chunks), "chunkTokenUsage": chunk_usage,
        })
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
