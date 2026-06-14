"""Stage: shallow-clone the PR repo and compute the diff."""
from __future__ import annotations

from typing import Any

from ...artifacts.builder import changed_files, write_json
from ...ado.client import resolve_branches
from ...git import ops as git_ops
from ..stage import Stage, StageContext


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


class PrepareRepositoryStage(Stage):
    """Clone the PR branches and write ``diff.patch`` + ``changed-files.json``."""

    name = "prepare_repository"

    def should_run(self, ctx: StageContext) -> bool:
        # The diff is needed for every downstream stage.
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        source, target = resolve_branches(cfg)
        state = git_ops.prepare_repo(cfg, source, target)
        ctx.state = state
        ctx.files_text = "\n".join(state.files) + "\n"

        # Persist diff and changed files into the artifact tree.
        ctx.artifacts.diff.write_text(state.diff_text, encoding="utf-8")
        write_json(ctx.artifacts.changed_files, changed_files(state.files))
        ctx.artifacts.commits.write_text(
            git_ops.run_git(state.repo_dir, "log", "--oneline", state.range_spec),
            encoding="utf-8",
        )

        _log(f"changed files: {len(state.files)}")
        _log(f"diff size: {len(state.diff_text.encode())} bytes")
        return {
            "files": len(state.files),
            "diff_bytes": len(state.diff_text.encode()),
            "source_branch": source,
            "target_branch": target,
        }


__all__ = ["PrepareRepositoryStage"]
