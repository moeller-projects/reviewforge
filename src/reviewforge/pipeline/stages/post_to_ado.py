"""Stage: post the calibrated findings to Azure DevOps.

This stage is a no-op when ``cfg.dry_run`` is true: it records the intent
in the run summary but never makes an ADO call. When posting, the stage
uses the isolated ``python -m reviewforge.ado.cli`` subprocess helper.
"""
from __future__ import annotations

import json
import shutil
from typing import Any

from ...ado.client import call_helper
from ...artifacts.builder import read_json
from ...artifacts.summary import finalize_run_summary
from ..stage import Stage, StageContext
from ..validation import validate_postable_review_doc


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


class PostToAdoStage(Stage):
    """Post the calibrated findings to Azure DevOps, or short-circuit on dry-run."""

    name = "post_to_ado"

    def should_run(self, ctx: StageContext) -> bool:
        # Always run so the summary captures the outcome (posted / skipped).
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.severity is None and ctx.artifacts.severity.exists():
            ctx.severity = read_json(ctx.artifacts.severity) or {"summary": "", "findings": []}
        # Preserve any existing final doc. Earlier stages may have already
        # appended extra findings to ``final-findings.json`` (for example,
        # AC coverage findings). Only fall back to severity when final is
        # missing, which covers the post-only path and standalone use.
        if ctx.artifacts.final.exists():
            ctx.final = read_json(ctx.artifacts.final) or {"summary": "", "findings": []}
        else:
            shutil.copyfile(ctx.artifacts.severity, ctx.artifacts.final)
            ctx.final = read_json(ctx.artifacts.final) or {"summary": "", "findings": []}

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
