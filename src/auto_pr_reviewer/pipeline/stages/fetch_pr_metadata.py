"""Stage: fetch PR metadata and write ``metadata.json``.

Wraps the legacy ``ado_review.py fetch-context`` subprocess into a
:class:`Stage`. The actual ADO REST call lives in the subprocess for
historical reasons; this stage just delegates and parses the result.
"""
from __future__ import annotations

import sys
from typing import Any

from ...ado.client import call_helper
from ...artifacts.builder import read_json
from ..stage import Stage, StageContext, StageStatus


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


class FetchPrMetadataStage(Stage):
    """Fetch the PR metadata, work items, and existing threads."""

    name = "fetch_pr_metadata"

    def should_run(self, ctx: StageContext) -> bool:
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.metadata:
            return {"cached": True, "pr_id": cfg.pr_id}
        _log(f"fetching Azure DevOps PR #{cfg.pr_id} context")
        call_helper(cfg, "fetch-context", ctx.artifacts.dir)
        # ``metadata.json`` is the first file the helper writes.
        metadata = read_json(ctx.artifacts.metadata) or {}
        ctx.metadata = metadata
        return {
            "pr_id": cfg.pr_id,
            "status": metadata.get("status"),
            "is_draft": bool(metadata.get("isDraft")),
        }


__all__ = ["FetchPrMetadataStage"]
