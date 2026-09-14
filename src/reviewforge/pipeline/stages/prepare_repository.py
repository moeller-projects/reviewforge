"""Stage: shallow-clone the PR repo and compute the diff."""
from __future__ import annotations

from dataclasses import replace
from typing import Any

from ...artifacts.builder import changed_files, write_json
from ...ado.client import resolve_branches
from ...git import ops as git_ops
from ...runlog import info as _log
from ..context_staging import stage_context_files
from ..review_state import ReviewMode
from ..stage import Stage, StageContext


def _apply_pr_file_scope(ctx: StageContext, state: Any) -> int:
    """Restrict the review diff to files ADO lists as changed in the PR.

    ADO computes the PR file set server-side with full history; the local
    shallow clone can disagree (rewritten target, stale merge base). Returns
    the number of excluded files. Fail-open: an empty ADO list or an empty
    intersection keeps the computed diff unchanged.
    """
    allowed = ctx.extras.get("pr_changed_files") or []
    if not allowed:
        return 0
    allowed_set = {str(path).lstrip("/") for path in allowed}
    kept = [f for f in state.files if f in allowed_set]
    removed = len(state.files) - len(kept)
    if not removed:
        return 0
    if not kept:
        _log(
            "[review][WARN] ADO PR changed-files list excludes every diff file; "
            "keeping the locally computed diff"
        )
        return 0
    _log(f"excluding {removed} file(s) not in the ADO PR changes: {sorted(set(state.files) - allowed_set)}")
    state.diff_text = git_ops.run_git(
        state.repo_dir, "diff", "--unified=3", "--no-ext-diff", state.range_spec, "--", *kept
    )
    state.files = kept
    return removed

class PrepareRepositoryStage(Stage):
    """Clone the PR branches and write ``diff.patch`` + ``changed-files.json``."""

    name = "prepare_repository"

    def should_run(self, ctx: StageContext) -> bool:
        return getattr(ctx.extras.get("review_state"), "mode", None) != "no_op"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        source, target = resolve_branches(cfg)
        review_state = ctx.extras.get("review_state")
        reviewed_commit = getattr(review_state, "last_reviewed_commit", None)
        if reviewed_commit:
            state = git_ops.prepare_repo(cfg, source, target, reviewed_commit=reviewed_commit)
        else:
            state = git_ops.prepare_repo(cfg, source, target)
        if review_state and reviewed_commit and not state.range_spec.startswith(f"{reviewed_commit}.."):
            updated = replace(
                review_state,
                mode=ReviewMode.FORCE_FULL,
                reason="previous review commit is not an ancestor of the current source",
            )
            ctx.extras["review_state"] = updated
            ctx.extras["review_context"] = updated.as_context()
        out_of_scope = _apply_pr_file_scope(ctx, state)
        ctx.state = state
        ctx.files_text = "\n".join(state.files) + "\n"

        # Persist diff and changed files into the artifact tree.
        ctx.artifacts.diff.write_text(state.diff_text, encoding="utf-8")
        write_json(ctx.artifacts.changed_files, changed_files(state.files))
        ctx.artifacts.commits.write_text(
            git_ops.run_git(state.repo_dir, "log", "--oneline", state.range_spec),
            encoding="utf-8",
        )
        stage_context_files(ctx, include_graph_context=False)

        _log(f"changed files: {len(state.files)}")
        _log(f"diff size: {len(state.diff_text.encode())} bytes")
        return {
            "files": len(state.files),
            "diff_bytes": len(state.diff_text.encode()),
            "source_branch": source,
            "target_branch": target,
            "files_out_of_scope": out_of_scope,
            "range_fallback_reason": getattr(state, "range_fallback_reason", ""),
        }


__all__ = ["PrepareRepositoryStage"]
