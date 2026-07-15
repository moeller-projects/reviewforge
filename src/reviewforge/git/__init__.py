"""Git operations subpackage."""
from __future__ import annotations

from .chunker import DiffChunk, build_chunks
from .ops import (
    GIT_ASKPASS_SCRIPT,
    RepoState,
    cleanup,
    log as git_log,
    prepare_repo,
    run_git,
    run_logged,
)

__all__ = [
    "DiffChunk",
    "GIT_ASKPASS_SCRIPT",
    "RepoState",
    "build_chunks",
    "cleanup",
    "git_log",
    "prepare_repo",
    "run_git",
    "run_logged",
]
