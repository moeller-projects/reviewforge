"""Stage: fetch PR metadata and write ``metadata.json``.

Invokes the isolated ``python -m reviewforge.ado.cli fetch-context`` subprocess from a
:class:`Stage`. The ADO REST call remains isolated in the subprocess; this stage delegates and parses the result.

This stage is also responsible for **loading the fetched context back
into the in-memory stage context** so downstream stages (intent, plan,
digest, review_diff, verify_findings, calibrate_severity) can see the
work items, work item comments, and existing PR threads. Without this
load step, the work-item-aware prompts operate on empty lists and the
pipeline produces false positives in its "Work item verification"
section (see ``docs/design/work-item-verification-false-positives.md``).
"""
from __future__ import annotations

from typing import Any

from ...ado.operations import fetch_pr_context
from ...artifacts.builder import read_json
from ...runlog import info as _log
from .detect_review_mode import DetectReviewModeStage
from ..stage import Stage, StageContext, StageStatus

# Test seam for the direct operation; no subprocess helper remains.
call_helper = fetch_pr_context




def _load_list(path: Any) -> list[Any] | None:
    if not path.exists():
        return None
    try:
        value = read_json(path)
    except (OSError, ValueError):
        return None
    return value if isinstance(value, list) else None


def _load_fetched_context(artifacts: Any) -> dict[str, Any]:
    """Load fetch-context artifacts into stage extras."""
    extras: dict[str, Any] = {}
    for key, path in (
        ("wi_context", artifacts.work_items),
        ("wi_comments_context", artifacts.work_items.with_name("work-item-comments.json")),
        ("thread_context", artifacts.threads),
    ):
        if (value := _load_list(path)) is not None:
            extras[key] = value
    return extras


class FetchPrMetadataStage(Stage):
    """Fetch the PR metadata, work items, and existing threads."""

    name = "fetch_pr_metadata"

    def should_run(self, ctx: StageContext) -> bool:
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.metadata:
            DetectReviewModeStage().run(ctx)
            return {"cached": True, "pr_id": cfg.pr_id}
        _log(f"fetching Azure DevOps PR #{cfg.pr_id} context")
        call_helper(cfg, ctx.artifacts.dir)
        # ``metadata.json`` is the first file the helper writes.
        metadata = read_json(ctx.artifacts.metadata) or {}
        ctx.metadata = metadata
        # Load the rest of the fetch-context artifacts back into the
        # in-memory stage context. Downstream stages read these via
        # ``ctx.extras.get("wi_context", [])`` etc. Without this, the
        # work-item-aware prompts operate on empty lists.
        ctx.extras.update(_load_fetched_context(ctx.artifacts))
        DetectReviewModeStage().run(ctx)
        return {
            "pr_id": cfg.pr_id,
            "status": metadata.get("status"),
            "is_draft": bool(metadata.get("isDraft")),
            "work_items_loaded": len(ctx.extras.get("wi_context", [])),
            "threads_loaded": len(ctx.extras.get("thread_context", [])),
        }


__all__ = ["FetchPrMetadataStage", "_load_fetched_context"]
