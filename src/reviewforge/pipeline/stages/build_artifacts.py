"""Stage: write the per-PR artifact layout and the system prompt file."""
from __future__ import annotations

from typing import Any

from ...ai.prompts import system_prompt
from ..stage import Stage, StageContext


class BuildArtifactsStage(Stage):
    """Persist the combined system prompt into the artifact directory."""

    name = "build_artifacts"

    def should_run(self, ctx: StageContext) -> bool:
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        text = system_prompt(cfg)
        ctx.artifacts.system_prompt.write_text(text, encoding="utf-8")
        # Legacy compat: ``ctx.system_prompt`` is also expected by older stages.
        ctx.extras.setdefault("system_prompt", text)
        return {
            "system_prompt_path": str(ctx.artifacts.system_prompt),
            "system_prompt_bytes": len(text.encode("utf-8")),
        }


__all__ = ["BuildArtifactsStage"]
