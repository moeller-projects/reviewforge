"""Stage: ask Pi to plan the deterministic context collection."""
from __future__ import annotations

from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


class PlanContextStage(Stage):
    name = "plan_context"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        text = stage_instruction(
            "context planning",
            cfg,
            ctx.artifacts.metadata,
            ctx.files_text,
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
            ctx.extras.get("paths", {}),
        ) + (ctx.state.diff_text if ctx.state else "")
        ctx.pi.run_json(
            cfg.context_plan_prompt_path, text, ctx.artifacts.plan, "context planning"
        )
        doc = read_json(ctx.artifacts.plan) or {}
        validate_stage(doc, StageLabel.CONTEXT_PLANNING)
        ctx.plan = doc
        return {
            "files_to_read": len(doc.get("files_to_read", [])),
            "searches": len(doc.get("searches_to_run", [])),
            "tests": len(doc.get("tests_to_inspect", [])),
        }


__all__ = ["PlanContextStage"]
