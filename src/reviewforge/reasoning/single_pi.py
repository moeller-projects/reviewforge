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
    """Keep every changed-file header while bounding one-call context."""
    if max_bytes <= 0 or len(diff_text.encode("utf-8")) <= max_bytes:
        return diff_text, False
    sections = diff_text.split("diff --git ")
    sections = [section for section in sections if section]
    if not sections:
        return diff_text.encode("utf-8")[:max_bytes].decode("utf-8", "ignore"), True
    headers = [f"diff --git {section.splitlines()[0]}" for section in sections]
    reserve = sum(len(header.encode("utf-8")) + 1 for header in headers)
    remaining = max(0, max_bytes - reserve)
    out: list[str] = []
    for index, section in enumerate(sections):
        header = headers[index]
        body = section[len(section.splitlines()[0]):].lstrip("\n")
        budget = remaining // (len(sections) - index) if remaining else 0
        body_bytes = body.encode("utf-8")[:budget]
        body_text = body_bytes.decode("utf-8", "ignore")
        out.append(f"{header}\n{body_text}".rstrip())
        remaining -= len(body_bytes)
    return "\n".join(out), True

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

        projection_started = time.perf_counter()
        self._synthesize_intermediate_artifacts(result, ctx)
        projection_duration_ms = int((time.perf_counter() - projection_started) * 1000)
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

    @staticmethod
    def _synthesize_intermediate_artifacts(result: ReviewResult, ctx: StageContext) -> None:
        """Write the legacy intermediate artifact files from a rich result.

        The single-call engine does not run the individual multi-stage steps,
        but downstream tooling and reviewers expect the same artifact layout.
        We synthesize the best-effort equivalents from the returned
        ``ReviewResult``.
        """
        pr_intent = result.pr_summary.intent or result.review_summary.summary

        intent_doc = {
            "pr_intent": pr_intent,
            "changed_behaviors": [],
            "risk_areas": [r for r in result.pr_summary.risk_assessment.splitlines() if r],
        }
        write_json(ctx.artifacts.intent, intent_doc)

        files_read: list[dict[str, str]] = []
        tests_read: list[str] = []
        work_items_read: list[str] = []
        seen_files: set[str] = set()
        for f in result.findings:
            ev = f.evidence
            for path in ev.relatedFiles:
                if path and path not in seen_files:
                    seen_files.add(path)
                    files_read.append({"path": path, "reason": "related to finding"})
            for t in ev.testsRead:
                if t and t not in tests_read:
                    tests_read.append(t)
            for w in ev.workItems:
                if w and w not in work_items_read:
                    work_items_read.append(w)
            for sym in ev.symbols:
                if sym.file and sym.file not in seen_files:
                    seen_files.add(sym.file)
                    files_read.append({"path": sym.file, "reason": f"symbol {sym.name}"})

        plan_doc = {
            "pr_intent": pr_intent,
            "files_to_read": files_read,
            "searches_to_run": [],
            "tests_to_inspect": tests_read,
        }
        write_json(ctx.artifacts.plan, plan_doc)

        collected_doc = {
            "files": [{"path": item["path"], "content": ""} for item in files_read],
            "tests": tests_read,
            "searches": [],
        }
        write_json(ctx.artifacts.collected, collected_doc)

        digest_doc = {
            "relevant_context": [],
            "possible_intentional_choices": result.pr_summary.positive_observations,
            "context_gaps": [u.topic for u in result.uncertainties],
        }
        write_json(ctx.artifacts.digest, digest_doc)


        legacy_findings = []
        for f in result.findings:
            ev = f.evidence
            context_files = list(
                dict.fromkeys(
                    list(ev.relatedFiles) + list(ev.testsRead) + list(ev.workItems)
                )
            )
            legacy_findings.append(
                {
                    "title": f.title,
                    "message": f"{f.observation} {f.impact} {f.recommendation}".strip(),
                    "severity": f.severity,
                    "file": f.file,
                    "line": f.line,
                    "confidence": f.confidence,
                    "contextBasis": f.contextBasis,
                    "suggestion": f.recommendation,
                    "evidence": {
                        "changedLines": list(ev.changedLines),
                        "contextFilesRead": context_files,
                        "whyNewInThisPr": ev.whyNewInThisPr,
                        "whyNotIntentional": ev.whyNotIntentional,
                        "classification": ev.classification,
                    },
                }
            )

        review_doc = {"summary": result.review_summary.summary, "findings": legacy_findings}
        write_json(ctx.artifacts.candidate, review_doc)
        write_json(ctx.artifacts.verified, review_doc)
        write_json(ctx.artifacts.severity, review_doc)
        write_json(ctx.artifacts.final, review_result_to_final_doc(result))


register_engine(SinglePiReasoningEngine().name, SinglePiReasoningEngine)
