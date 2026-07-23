"""Stage: ask Pi to reconstruct the PR intent from the diff and metadata."""
from __future__ import annotations

from typing import Any
import sys

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


class ReconstructIntentStage(Stage):
    name = "reconstruct_intent"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        text = stage_instruction(
            "intent reconstruction",
            cfg,
            ctx.artifacts.metadata,
            ctx.files_text,
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
            ctx.extras.get("paths", {}),
        ) + (ctx.state.diff_text if ctx.state else "")
        ctx.pi.run_json(cfg.intent_prompt_path, text, ctx.artifacts.intent, "intent reconstruction")
        ctx.last_token_usage = ctx.pi.last_tokens
        doc = read_json(ctx.artifacts.intent) or {}
        validate_stage(doc, StageLabel.INTENT_RECONSTRUCTION)
        ctx.intent = doc
        return {
            "pr_intent": doc.get("pr_intent", "")[:80],
            "risk_areas": len(doc.get("risk_areas", [])),
        }


__all__ = ["ReconstructIntentStage"]
