"""Stage: execute the selected reasoning engine and materialize results."""
from __future__ import annotations

from typing import Any

from ...artifacts.builder import write_json
from ...reasoning.engine import get_engine
from ..projection import review_result_to_final_doc
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
        ctx.review_result = result

        write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))
        final_doc = review_result_to_final_doc(result)
        write_json(ctx.artifacts.final, final_doc)
        ctx.final = final_doc

        return {
            "engine": engine.name,
            "findings": len(result.findings),
            "review_result": str(ctx.artifacts.review_result),
            "final_findings": str(ctx.artifacts.final),
            "metrics": result.metrics.model_dump(by_alias=True, exclude_none=False),
        }


__all__ = ["ExecuteReasoningEngineStage"]
