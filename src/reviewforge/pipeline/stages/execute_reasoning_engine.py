"""Stage: execute the selected reasoning engine and materialize results."""
from __future__ import annotations

from typing import Any

from ...artifacts.builder import write_json
from ...reasoning.engine import get_engine
from ... import __version__
from ...runlog import warning
from ...artifacts.builder import write_json
from ...reasoning.engine import get_engine
from ..projection import review_result_to_final_doc
from ..review_state import filter_dismissed_findings
from ..sarif import review_result_to_sarif
from ..schemas import ReviewResult
from ..stage import Stage, StageContext


class ExecuteReasoningEngineStage(Stage):
    """Select and run a :class:`~reviewforge.reasoning.ReasoningEngine`."""

    name = "execute_reasoning_engine"

    def should_run(self, ctx: StageContext) -> bool:
        return getattr(ctx.extras.get("review_state"), "mode", None) != "no_op"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        engine = get_engine(ctx.cfg.reasoning_engine, ctx.cfg)
        result = engine.execute(ctx)
        feedback = getattr(ctx.extras.get("review_state"), "feedback", ())
        if feedback:
            payload = result.model_dump(by_alias=True, exclude_none=False)
            payload["findings"], discarded = filter_dismissed_findings(payload["findings"], feedback)
            payload["discarded_findings"] = payload.get("discarded_findings", []) + discarded
            result = ReviewResult.model_validate(payload)
        ctx.review_result = result

        if not ctx.artifacts.review_result.exists():
            write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))
        sarif_written = False
        try:
            write_json(
                ctx.artifacts.sarif,
                review_result_to_sarif(result, tool_version=__version__),
            )
            sarif_written = True
        except Exception as exc:  # noqa: BLE001 - observability must not fail reviews
            warning(f"failed to write SARIF findings: {type(exc).__name__}: {exc}")
        final_doc = ctx.final or review_result_to_final_doc(result)
        if not ctx.artifacts.final.exists():
            write_json(ctx.artifacts.final, final_doc)
        ctx.final = final_doc

        details: dict[str, Any] = {
            "engine": engine.name,
            "findings": len(result.findings),
            "review_result": str(ctx.artifacts.review_result),
            "final_findings": str(ctx.artifacts.final),
            "metrics": result.metrics.model_dump(by_alias=True, exclude_none=False),
        }
        if sarif_written:
            details["sarif_findings"] = str(ctx.artifacts.sarif)
        return details


__all__ = ["ExecuteReasoningEngineStage"]
