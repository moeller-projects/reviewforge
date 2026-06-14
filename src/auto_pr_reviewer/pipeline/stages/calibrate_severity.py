"""Stage: ask Pi to recalibrate finding severities using the digest."""
from __future__ import annotations

from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


class CalibrateSeverityStage(Stage):
    name = "calibrate_severity"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        _log("running severity calibration stage")
        text = (
            stage_instruction(
                "severity calibration",
                cfg,
                ctx.artifacts.metadata,
                ctx.files_text,
                ctx.extras.get("wi_context", []),
                ctx.extras.get("thread_context", []),
                ctx.extras.get("paths", {}),
            )
            + (ctx.state.diff_text if ctx.state else "")
        )
        ctx.pi.run_json(cfg.severity_prompt_path, text, ctx.artifacts.severity, "severity calibration")
        doc = read_json(ctx.artifacts.severity) or {"summary": "", "findings": []}
        validate_stage(doc, StageLabel.SEVERITY_CALIBRATION)
        ctx.severity = doc
        return {"findings": len(doc.get("findings", []))}


__all__ = ["CalibrateSeverityStage"]
