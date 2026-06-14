"""Stage: adversarially verify candidate findings (drop false positives)."""
from __future__ import annotations

import shutil
from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


class VerifyFindingsStage(Stage):
    name = "verify_findings"

    def should_run(self, ctx: StageContext) -> bool:
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if not cfg.verify_findings:
            _log("VERIFY_FINDINGS=0; skipping verification stage")
            shutil.copyfile(ctx.artifacts.candidate, ctx.artifacts.verified)
            doc = read_json(ctx.artifacts.verified) or {"summary": "", "findings": []}
            ctx.verified = doc
            return {"findings": len(doc.get("findings", [])), "skipped": True}
        _log("running adversarial finding verification stage")
        text = (
            stage_instruction(
                "finding verification",
                cfg,
                ctx.artifacts.metadata,
                ctx.files_text,
                ctx.extras.get("wi_context", []),
                ctx.extras.get("thread_context", []),
                ctx.extras.get("paths", {}),
            )
            + (ctx.state.diff_text if ctx.state else "")
        )
        ctx.pi.run_json(cfg.verify_prompt_path, text, ctx.artifacts.verified, "finding verification")
        ctx.last_token_usage = ctx.pi.last_tokens
        doc = read_json(ctx.artifacts.verified) or {"summary": "", "findings": []}
        validate_stage(doc, StageLabel.FINDING_VERIFICATION)
        ctx.verified = doc
        return {"findings": len(doc.get("findings", []))}


__all__ = ["VerifyFindingsStage"]
