"""Map a finding's ``(file, new_line)`` to an Azure DevOps thread position.

ADO ``threadContext`` requires ``rightFileStart`` (and optionally
``rightFileEnd``) to attach a comment to a specific diff hunk. This module
parses a unified diff and builds an in-memory index that maps each changed
line in the new file to its hunk's start position. When an exact mapping is
not possible, the function returns ``None`` so the caller can fall back to a
file-level or summary comment.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable
import re


@dataclass(frozen=True)
class AdoThreadContext:
    """The subset of ADO ``threadContext`` needed to anchor an inline comment.

    ``file_path`` is the file path as ADO expects (with a leading ``/``).
    ``right_file_start`` and ``right_file_end`` are the line numbers in the
    new file. ``position`` is a placeholder for cases where ADO needs the
    diff hunk index instead of a line number.

    All three line-anchoring fields are optional. When they are all
    ``None`` the serialized form is just ``{"filePath": "..."}`` — ADO
    accepts that as a file-level comment (attached to the file header in
    the Files tab, not to a specific line). This is the fallback path
    for findings on files that appear in the diff but where no usable
    hunk can be anchored (mode-only changes, renames, binary files).
    """

    file_path: str
    right_file_start: int | None = None
    right_file_end: int | None = None
    position: int | None = None

    @property
    def is_file_level(self) -> bool:
        """``True`` when no line anchor is set (file-level comment)."""
        return self.right_file_start is None and self.right_file_end is None

    def to_thread_context(self) -> dict[str, Any]:
        """Serialize to the dict shape expected by ADO's ``threadContext``.

        File-level contexts (no line numbers) emit just ``filePath``;
        inline contexts include ``rightFileStart`` / ``rightFileEnd`` and
        optionally ``position``.
        """
        ctx: dict[str, Any] = {"filePath": self.file_path}
        if self.right_file_start is not None:
            ctx["rightFileStart"] = {"line": self.right_file_start, "offset": 1}
        if self.right_file_end is not None:
            ctx["rightFileEnd"] = {"line": self.right_file_end, "offset": 1}
        if self.position is not None:
            ctx["position"] = self.position
        return ctx


# ---------------------------------------------------------------------------
# Diff parsing
# ---------------------------------------------------------------------------

_HUNK_RE = re.compile(
    r"^@@\s+-(?P<old_start>\d+)(?:,(?P<old_count>\d+))?\s+\+(?P<new_start>\d+)(?:,(?P<new_count>\d+))?\s+@@"
)
_FILE_HEADER_RE = re.compile(r"^\+\+\+\s+(?P<path>.+)$")


@dataclass
class _FileDiff:
    path: str
    hunks: list[dict[str, Any]] = field(default_factory=list)

    def add_hunk(
        self, *, old_start: int, old_count: int, new_start: int, new_count: int
    ) -> None:
        self.hunks.append(
            {
                "old_start": old_start,
                "old_count": old_count,
                "new_start": new_start,
                "new_count": new_count,
                "lines": [],  # list[(kind, content)]; kind in {" ", "-", "+"}
            }
        )

    def add_line(self, kind: str, content: str) -> None:
        if not self.hunks:
            return
        self.hunks[-1]["lines"].append((kind, content))


def parse_unified_diff(diff_text: str) -> list[_FileDiff]:
    """Parse a unified diff into one :class:`_FileDiff` per file."""
    files: list[_FileDiff] = []
    current: _FileDiff | None = None
    for raw_line in diff_text.splitlines():
        if raw_line.startswith("+++ "):
            m = _FILE_HEADER_RE.match(raw_line)
            if not m:
                continue
            path = m.group("path").strip()
            if path.startswith("b/"):
                path = path[2:]
            current = _FileDiff(path=path)
            files.append(current)
            continue
        if raw_line.startswith("--- "):
            # ignore old-file headers; the new-file header carries the path
            continue
        if raw_line.startswith("@@"):
            m = _HUNK_RE.match(raw_line)
            if not m or current is None:
                continue
            current.add_hunk(
                old_start=int(m.group("old_start")),
                old_count=int(m.group("old_count") or 1),
                new_start=int(m.group("new_start")),
                new_count=int(m.group("new_count") or 1),
            )
            continue
        if current is None or not current.hunks:
            continue
        if raw_line.startswith("+"):
            current.add_line("+", raw_line[1:])
        elif raw_line.startswith("-"):
            current.add_line("-", raw_line[1:])
        elif raw_line.startswith(" "):
            current.add_line(" ", raw_line[1:])
        elif raw_line.startswith("\\"):
            # "\ No newline at end of file" — ignored for line mapping.
            continue
    return files


# ---------------------------------------------------------------------------
# Mapping
# ---------------------------------------------------------------------------


@dataclass
class DiffLineMapper:
    """Index a parsed diff for fast ``(file, line) → position`` lookups.

    Build with :meth:`from_text` (parse + index in one call) or :meth:`from_files`
    for a pre-parsed diff. The mapper caches a per-file index of
    ``new_line → hunk_start_line`` so that any new line in a changed hunk
    resolves to the right ADO position.
    """

    _files: list[_FileDiff]
    _index: dict[str, dict[int, tuple[int, int, int]]] = field(default_factory=dict)

    @classmethod
    def from_text(cls, diff_text: str) -> "DiffLineMapper":
        files = parse_unified_diff(diff_text)
        return cls(_files=files)

    @property
    def files(self) -> list[str]:
        return [f.path for f in self._files]

    def _build_file_index(self, file_diff: _FileDiff) -> dict[int, tuple[int, int, int]]:
        """Return a map ``new_line → (right_file_start, right_file_end, hunk_index)``.

        Each line in the new file maps to itself (start = end = new_line). For
        added lines, the entire contiguous block of added lines is also
        recorded under the block's start, so a fallback lookup can land on
        the start of the block when the exact line is not found.
        """
        out: dict[int, tuple[int, int, int]] = {}
        for hunk_index, hunk in enumerate(file_diff.hunks):
            new_line = hunk["new_start"]
            block_start: int | None = None
            block_end: int | None = None
            for kind, _ in hunk["lines"]:
                if kind in {" ", "+"}:
                    if kind == "+":
                        if block_start is None:
                            block_start = new_line
                        block_end = new_line
                        out[new_line] = (new_line, new_line, hunk_index + 1)
                    new_line += 1
                # "-" lines do not advance the new-file line counter.
            # Expose the start of the added block under itself for fast
            # fallback (e.g. when the reviewer references the block as a
            # whole but provides a non-added line number).
            if block_start is not None and block_end is not None:
                out[block_start] = (block_start, block_end, hunk_index + 1)
        return out

    def _index_for(self, file_path: str) -> dict[int, tuple[int, int, int]] | None:
        normalized = _normalize_path(file_path)
        if normalized in self._index:
            return self._index[normalized]
        for f in self._files:
            if _normalize_path(f.path) == normalized:
                idx = self._build_file_index(f)
                self._index[normalized] = idx
                return idx
        return None

    def find(
        self, file_path: str, new_line: int | None
    ) -> AdoThreadContext | None:
        """Return the ADO thread context for ``(file_path, new_line)`` or ``None``.

        Returns ``None`` when:

        * ``new_line`` is falsy
        * the file is not in the diff
        * the line is not part of an added/kept block in a hunk
        """
        if not new_line or not file_path:
            return None
        idx = self._index_for(file_path)
        if not idx:
            return None
        # Prefer exact line match.
        if new_line in idx:
            start, end, position = idx[new_line]
            return AdoThreadContext(
                file_path=_with_leading_slash(file_path),
                right_file_start=start,
                right_file_end=end,
                position=position,
            )
        # Fall back: find the closest hunk above this line.
        candidates = [(line, v) for line, v in idx.items() if line <= new_line]
        if not candidates:
            return None
        candidates.sort(key=lambda item: item[0])
        _, (start, end, position) = candidates[-1]
        return AdoThreadContext(
            file_path=_with_leading_slash(file_path),
            right_file_start=start,
            right_file_end=end,
            position=position,
        )

    def line_set(self, file_path: str) -> set[int]:
        """Return all new-file line numbers touched in any hunk for ``file_path``.

        Used by the stale-comment reconciliation pass: a bot thread
        anchored at ``(file, line)`` is considered current when
        ``line in line_set(file)``. Empty set when the file is not in
        the diff at all (i.e. the file was removed entirely → every
        prior anchor on it is stale).
        """
        normalized = _normalize_path(file_path)
        out: set[int] = set()
        for f in self._files:
            if _normalize_path(f.path) != normalized:
                continue
            for hunk in f.hunks:
                line = hunk["new_start"]
                for kind, _content in hunk["lines"]:
                    if kind in {" ", "+"}:
                        out.add(line)
                        line += 1
                    # "-" lines do not advance the new-file line counter.
            break
        return out

    def file_level_context(self, file_path: str) -> AdoThreadContext | None:
        """Return a file-level thread context for ``file_path`` if the file appears in the diff.

        Two flavours:

        * **Inline fallback** — file is in the diff with at least one hunk;
          return a context anchored to the first hunk's start so ADO can
          place the comment on a real line.
        * **Pure file-level** — file is in the diff but has no usable
          hunks (mode-only ``chmod`` change, rename, binary file, etc.).
          Return a context with just ``filePath`` so ADO attaches the
          comment to the file header in the Files tab rather than
          rejecting the request with HTTP 400.

        Returns ``None`` only when the file is not in the diff at all.
        """
        if not file_path:
            return None
        for f in self._files:
            if _normalize_path(f.path) == _normalize_path(file_path):
                if not f.hunks:
                    # No content lines changed (mode-only, rename,
                    # binary). ADO still accepts a threadContext with
                    # just filePath — better than silently dropping the
                    # finding on the floor.
                    return AdoThreadContext(
                        file_path=_with_leading_slash(file_path),
                    )
                h0 = f.hunks[0]
                return AdoThreadContext(
                    file_path=_with_leading_slash(file_path),
                    right_file_start=h0["new_start"],
                    right_file_end=h0["new_start"],
                    position=1,
                )
        return None


# ---------------------------------------------------------------------------
# Module-level convenience
# ---------------------------------------------------------------------------


def map_file_line_to_diff_position(
    file_path: str,
    new_line: int | None,
    diff_text: str | None = None,
    *,
    mapper: DiffLineMapper | None = None,
) -> AdoThreadContext | None:
    """Public entrypoint matching the API specified in the task.

    Pass either ``diff_text`` (re-parsed on every call) or a pre-built
    ``mapper`` (cached for many calls in a single run). If neither is
    available, the function returns ``None`` so the caller can fall back to
    a file-level or summary comment.
    """
    if mapper is None:
        if not diff_text:
            return None
        mapper = DiffLineMapper.from_text(diff_text)
    return mapper.find(file_path, new_line)


def map_file_to_fallback(
    file_path: str,
    diff_text: str | None = None,
    *,
    mapper: DiffLineMapper | None = None,
) -> AdoThreadContext | None:
    """Return a file-level context for ``file_path`` if it appears in the diff."""
    if mapper is None:
        if not diff_text:
            return None
        mapper = DiffLineMapper.from_text(diff_text)
    return mapper.file_level_context(file_path)


def collect_changed_files(diff_text: str) -> list[str]:
    """Return the list of files mentioned in the diff (new-file paths)."""
    return [f.path for f in parse_unified_diff(diff_text)]


def line_set_for_file(diff_text: str, file_path: str) -> set[int]:
    """Return the set of new-file line numbers touched in ``file_path``'s diff.

    Convenience wrapper around :meth:`DiffLineMapper.line_set` for
    callers that have only the diff text and a single file to query
    (e.g. the stale-comment reconciliation pass).
    """
    return DiffLineMapper.from_text(diff_text).line_set(file_path)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _normalize_path(p: str) -> str:
    return p.lstrip("/").replace("\\", "/")


def _with_leading_slash(p: str) -> str:
    """ADO expects file paths to start with ``/``."""
    p = p.replace("\\", "/")
    return p if p.startswith("/") else f"/{p}"


__all__ = [
    "AdoThreadContext",
    "DiffLineMapper",
    "collect_changed_files",
    "line_set_for_file",
    "map_file_line_to_diff_position",
    "map_file_to_fallback",
    "parse_unified_diff",
]
