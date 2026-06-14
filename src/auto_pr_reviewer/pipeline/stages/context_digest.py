"""Stage: ask Pi to digest the collected context into structured notes."""
from __future__ import annotations

from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


class ContextDigestStage(Stage):
    name = "context_digest"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        text = stage_instruction(
            "context digest",
            cfg,
            ctx.artifacts.metadata,
            ctx.files_text,
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
            ctx.extras.get("paths", {}),
        ) + (ctx.state.diff_text if ctx.state else "")
        ctx.pi.run_json(
            cfg.context_digest_prompt_path, text, ctx.artifacts.digest, "context digest"
        )
        ctx.last_token_usage = ctx.pi.last_tokens
        doc = read_json(ctx.artifacts.digest) or {}
        validate_stage(doc, StageLabel.CONTEXT_DIGEST)
        ctx.digest = doc
        return {
            "relevant_context": len(doc.get("relevant_context", [])),
            "intentional_choices": len(doc.get("possible_intentional_choices", [])),
        }


__all__ = ["ContextDigestStage"]
