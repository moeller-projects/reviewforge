"""Stage: select review mode before repository preparation or Pi."""
from __future__ import annotations

from typing import Any

from ..review_state import ReviewerIdentity, ReviewMode, ReviewState, select_review_state
from ..stage import Stage, StageContext


class DetectReviewModeStage(Stage):
    name = "detect_review_mode"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        payload = ctx.metadata.get("reviewState") or ctx.extras.get("review_state_payload") or {}
        raw_reviewer = payload.get("reviewer") if isinstance(payload, dict) else None
        reviewer = None
        if isinstance(raw_reviewer, dict) and raw_reviewer.get("id"):
            reviewer = ReviewerIdentity(
                user_id=str(raw_reviewer["id"]),
                display_name=str(raw_reviewer.get("displayName") or ""),
                unique_name=str(raw_reviewer.get("uniqueName") or ""),
                descriptor=str(raw_reviewer.get("descriptor") or ""),
            )
        threads = payload.get("threads") if isinstance(payload, dict) else None
        commits = payload.get("commits") if isinstance(payload, dict) else None
        state = select_review_state(
            reviewer=reviewer,
            threads=threads if isinstance(threads, list) else ctx.extras.get("thread_context", []),
            commits=commits if isinstance(commits, list) else [],
            current_commit=str(payload.get("currentCommit") or ctx.metadata.get("sourceCommit") or "") or None,
            force_full=bool(getattr(ctx.cfg, "force_full_review", False)),
        )
        ctx.extras["review_state"] = state
        ctx.extras["review_context"] = state.as_context()
        if state.mode is ReviewMode.NO_OP:
            ctx.skip_reason = "No new commits since the previous review.\n\nSkipping review."
            ctx.final = {"summary": ctx.skip_reason, "findings": []}
            from ...artifacts.builder import write_json
            write_json(ctx.artifacts.final, ctx.final)
        return {"mode": state.mode.value, "reason": state.reason, "previous_comments": len(state.previous_comments)}


__all__ = ["DetectReviewModeStage"]
