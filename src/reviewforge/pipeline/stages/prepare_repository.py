"""Stage: shallow-clone the PR repo and compute the diff."""
from __future__ import annotations

from dataclasses import replace
from typing import Any

from ...artifacts.builder import changed_files, write_json
from ...ado.client import resolve_branches
from ...exceptions import InputError
from ...git import ops as git_ops
from ...runlog import info as _log
from ..context_staging import stage_context_files
from ..review_state import ReviewMode
from ..stage import Stage, StageContext


def _apply_pr_file_scope(ctx: StageContext, state: Any) -> int:
    """Restrict the local diff to ADO's authoritative changed-file set."""
    if "pr_changed_files" not in ctx.extras:
        ctx.extras["scope_status"] = "unavailable"
        return 0
    allowed = ctx.extras["pr_changed_files"]
    if not allowed:
        ctx.extras["scope_status"] = "empty"
        return 0
    ctx.extras["scope_source"] = "ado-pr-changed-files"
    allowed_set = {str(path).lstrip("/") for path in allowed}
    kept = [f for f in state.files if f in allowed_set]
    removed = len(state.files) - len(kept)
    if not kept:
        ctx.extras["scope_status"] = "contradictory"
        raise InputError(
            "ADO changed-files scope excludes every file in the local diff",
            details={"local_files": state.files, "ado_files": sorted(allowed_set)},
        )
    ctx.extras["scope_status"] = "scoped" if removed else "complete"
    if not removed:
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
        if review_state and reviewed_commit and state.range_mode == "full-rebase":
            updated = replace(
                review_state,
                mode=ReviewMode.FORCE_FULL,
                reason=state.range_fallback_reason
                or "previous review commit is not an ancestor of the current source",
            )
            ctx.extras["review_state"] = updated
            ctx.extras["review_context"] = updated.as_context()
        ctx.state = state
        local_file_count = len(state.files)
        out_of_scope = _apply_pr_file_scope(ctx, state)
        ctx.extras["local_file_count"] = local_file_count
        ctx.extras["range_spec"] = state.range_spec
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
            "local_files": ctx.extras.get("local_file_count", len(state.files)),
            "diff_bytes": len(state.diff_text.encode()),
            "source_branch": source,
            "target_branch": target,
            "files_out_of_scope": out_of_scope,
            "scope_status": ctx.extras.get("scope_status", "unavailable"),
            "scope_source": ctx.extras.get("scope_source"),
            "range_spec": state.range_spec,
            "range_mode": getattr(state, "range_mode", "full"),
            "range_fallback_reason": getattr(state, "range_fallback_reason", ""),
        }


__all__ = ["PrepareRepositoryStage"]
