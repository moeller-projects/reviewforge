"""Stage: post the calibrated findings to Azure DevOps.

This stage is a no-op when ``cfg.dry_run`` is true: it records the intent
in the run summary but never makes an ADO call. When posting, the stage
uses the isolated ``python -m reviewforge.ado.cli`` subprocess helper.
"""
from __future__ import annotations

import json
from typing import Any

from ...ado.client import call_helper
from ...artifacts.builder import read_json
from ...artifacts.summary import finalize_run_summary
from ...runlog import info as _log
from ..stage import Stage, StageContext
from ..validation import validate_postable_review_doc





class PostToAdoStage(Stage):
    """Post the calibrated findings to Azure DevOps, or short-circuit on dry-run."""

    name = "post_to_ado"

    def should_run(self, ctx: StageContext) -> bool:
        # Always run so the summary captures the outcome (posted / skipped).
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.final is None:
            if ctx.review_result is None:
                raise RuntimeError("[review][ERROR] no postable review document in context")
            from ..projection import review_result_to_final_doc
            ctx.final = review_result_to_final_doc(ctx.review_result)
        if not ctx.artifacts.final.exists():
            from ...artifacts.builder import write_json
            write_json(ctx.artifacts.final, ctx.final)

        validate_postable_review_doc(ctx.final)
        if cfg.dry_run:
            _log("DRY_RUN=1; printing findings JSON (not posting)")
            print(json.dumps(ctx.final, ensure_ascii=False))
            ctx.posted = {"created": 0, "skipped": 0, "dry_run": 1}
            from ...artifacts.builder import write_json as _write
            _write(ctx.artifacts.posted, ctx.posted)
            return {"dry_run": True, "findings": len(ctx.final.get("findings", []))}

        _log(f"posting findings PR #{cfg.pr_id} via Python ADO helper")
        call_helper(cfg, "post-findings", ctx.artifacts.dir, findings=ctx.artifacts.final)
        posted_path = ctx.artifacts.dir / "posted-findings.json"
        if posted_path.exists():
            try:
                ctx.posted = read_json(posted_path) or {}
            except Exception:
                ctx.posted = {}
        return {
            "posted": ctx.posted,
            "findings": len(ctx.final.get("findings", [])),
        }


__all__ = ["PostToAdoStage"]
