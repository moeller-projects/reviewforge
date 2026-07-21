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

import sys
from typing import Any

from ...ado.client import call_helper
from ...artifacts.builder import read_json
from .detect_review_mode import DetectReviewModeStage
from ..stage import Stage, StageContext, StageStatus


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


def _load_fetched_context(artifacts: Any) -> dict[str, Any]:
    """Load the four fetch-context artifacts into a dict for ``ctx.extras``.

    The fetch-context subprocess writes:

    * ``work-items.json`` → list of work item dicts (``wi_context``)
    * ``work-item-comments.json`` → list of
      ``{"workItemId": str, "comments": [...]}`` (``wi_comments_context``)
    * ``threads.json`` → list of simplified thread dicts (``thread_context``)

    Returns a dict that can be ``update()``-ed into ``ctx.extras``. Missing
    or malformed files are skipped silently — downstream stages default to
    empty lists via ``ctx.extras.get(..., [])`` so the pipeline still runs
    when the fetch-context step was skipped (e.g., a rerun with cached
    metadata, or a fetch failure).

    Note: ``work-item-comments.json`` is not declared in
    :data:`reviewforge.artifacts.manager.ARTIFACT_NAMES` and therefore
    not on the :class:`Artifacts` dataclass. It is derived from the
    ``work_items`` path here. If the artifact contract is later updated
    to declare the comments file, replace this with
    ``artifacts.work_item_comments``.
    """
    extras: dict[str, Any] = {}
    if artifacts.work_items.exists():
        try:
            wi = read_json(artifacts.work_items)
        except (OSError, ValueError):
            wi = None
        if isinstance(wi, list):
            extras["wi_context"] = wi
    # work-item-comments.json is not on the Artifacts dataclass (see note
    # above). Derive its path from the sibling work-items.json.
    wi_comments_path = artifacts.work_items.with_name("work-item-comments.json")
    if wi_comments_path.exists():
        try:
            wic = read_json(wi_comments_path)
        except (OSError, ValueError):
            wic = None
        if isinstance(wic, list):
            extras["wi_comments_context"] = wic
    if artifacts.threads.exists():
        try:
            th = read_json(artifacts.threads)
        except (OSError, ValueError):
            th = None
        if isinstance(th, list):
            extras["thread_context"] = th
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
        call_helper(cfg, "fetch-context", ctx.artifacts.dir)
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
