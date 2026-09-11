"""Stage: execute the selected reasoning engine and materialize results."""
from __future__ import annotations

from typing import Any

from ... import __version__
from ...artifacts.builder import write_json
from ...artifacts.builder import read_json
from ...reasoning.engine import get_engine
from ...runlog import warning
from ..projection import review_result_to_final_doc
from ..review_state import filter_dismissed_findings
from ..sarif import review_result_to_sarif
from ..schemas import ReviewResult
from ..stage import Stage, StageContext


def _apply_feedback(result: ReviewResult, ctx: StageContext) -> ReviewResult:
    feedback = getattr(ctx.extras.get("review_state"), "feedback", ())
    if not feedback:
        return result
    payload = result.model_dump(by_alias=True, exclude_none=False)
    payload["findings"], discarded = filter_dismissed_findings(payload["findings"], feedback)
    payload["discarded_findings"] = payload.get("discarded_findings", []) + discarded
    return ReviewResult.model_validate(payload)


def _write_sarif(ctx: StageContext, result: ReviewResult) -> bool:
    try:
        write_json(
            ctx.artifacts.sarif,
            review_result_to_sarif(
                result,
                tool_version=__version__,
                repo_url=f"https://dev.azure.com/{ctx.cfg.ado_org}/{ctx.cfg.ado_project}/_git/{ctx.cfg.ado_repo_id}",
                pr_id=ctx.cfg.pr_id,
            ),
        )
        return True
    except Exception as exc:
        warning(f"failed to write SARIF findings: {type(exc).__name__}: {exc}")
        return False


def _write_final(ctx: StageContext, result: ReviewResult) -> dict[str, Any]:
    final = ctx.final or review_result_to_final_doc(result)
    if not ctx.artifacts.final.exists():
        write_json(ctx.artifacts.final, final)
    ctx.final = final
    return final


def _execution_details(ctx: StageContext, engine: Any, result: ReviewResult, sarif_written: bool) -> dict[str, Any]:
    details: dict[str, Any] = {
        "engine": engine.name,
        "findings": len(result.findings),
        "test_gaps": len(result.test_gaps),
        "escalation_hints": len(result.escalation_hints),
        "discarded_findings": len(result.discarded_findings),
        "review_result": str(ctx.artifacts.review_result),
        "final_findings": str(ctx.artifacts.final),
        "metrics": result.metrics.model_dump(by_alias=True, exclude_none=False),
    }
    reads = getattr(ctx.pi, "context_file_reads", None)
    if isinstance(reads, (dict, str)):
        details["context_file_reads"] = reads
    if ctx.extras.get("_synthesis_fallback"):
        details["synthesisFallback"] = True
    counts = ctx.extras.get("_finding_counts")
    if isinstance(counts, dict):
        details["finding_counts"] = {key: int(counts.get(key, 0) or 0) for key in ("candidate", "verified", "severity", "final")}
    if sarif_written:
        details["sarif_findings"] = str(ctx.artifacts.sarif)
    return details


class ExecuteReasoningEngineStage(Stage):
    """Select and run a :class:`~reviewforge.reasoning.ReasoningEngine`."""

    name = "execute_reasoning_engine"

    def should_run(self, ctx: StageContext) -> bool:
        return getattr(ctx.extras.get("review_state"), "mode", None) != "no_op"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        set_working_dir = getattr(ctx.pi, "set_working_dir", None)
        if callable(set_working_dir):
            set_working_dir(getattr(ctx.state, "repo_dir", None))
        engine = get_engine(ctx.cfg.reasoning_engine, ctx.cfg)
        result = _apply_feedback(engine.execute(ctx), ctx)
        ctx.review_result = result
        if not ctx.artifacts.review_result.exists():
            write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))
        sarif_written = _write_sarif(ctx, result)
        _write_final(ctx, result)
        return _execution_details(ctx, engine, result, sarif_written)


__all__ = ["ExecuteReasoningEngineStage"]
