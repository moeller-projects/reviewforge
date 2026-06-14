"""Split a large diff into file-based chunks for the model to review in pieces."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .ops import RepoState, run_git


@dataclass(frozen=True)
class DiffChunk:
    """A bounded slice of the diff plus the list of files it contains."""

    diff_text: str
    files_text: str
    truncated: bool = False


def build_chunks(state: RepoState, max_bytes: int) -> tuple[list[DiffChunk], bool]:
    """Greedy file-by-file packing into chunks of at most ``max_bytes`` bytes.

    If a single file's diff exceeds ``max_bytes``, that file's chunk is hard
    truncated and flagged with ``truncated=True``. Returns ``(chunks, any_truncated)``.
    """
    chunks: list[DiffChunk] = []
    current_diff = ""
    current_files: list[str] = []
    truncated_any = False
    for file in state.files:
        file_diff = run_git(
            state.repo_dir,
            "diff",
            "--unified=3",
            "--no-ext-diff",
            state.range_spec,
            "--",
            file,
        )
        size = len(file_diff.encode())
        if size > max_bytes:
            if current_files:
                chunks.append(DiffChunk(current_diff, "\n".join(current_files) + "\n"))
                current_diff = ""
                current_files = []
            truncated_any = True
            chunks.append(
                DiffChunk(
                    file_diff.encode()[:max_bytes].decode(errors="ignore")
                    + f"\n\n[FILE DIFF TRUNCATED: {file} original size {size} bytes, cap {max_bytes} bytes]\n",
                    file + "\n",
                    True,
                )
            )
            continue
        if current_diff and len((current_diff + file_diff).encode()) > max_bytes:
            chunks.append(DiffChunk(current_diff, "\n".join(current_files) + "\n"))
            current_diff = ""
            current_files = []
        current_diff += file_diff
        current_files.append(file)
    if current_files:
        chunks.append(DiffChunk(current_diff, "\n".join(current_files) + "\n"))
    return chunks, truncated_any


__all__ = ["DiffChunk", "build_chunks"]
