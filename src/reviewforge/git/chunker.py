"""Split a large diff into file-based chunks for the model to review in pieces."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .ops import RepoState, run_git


@dataclass(frozen=True)
class DiffChunk:
    """A bounded review scope plus the list of files it owns."""

    diff_text: str
    files_text: str
    truncated: bool = False
    scope_id: str = ""
    context_files: tuple[str, ...] = ()
    affected_flows: tuple[str, ...] = ()
    bridge_files: tuple[str, ...] = ()


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


def build_scopes(
    diff_text: str,
    files: list[str],
    max_bytes: int,
    *,
    crg_analysis: dict[str, object] | None = None,
    graph_context: dict[str, object] | None = None,
) -> tuple[list[DiffChunk], bool]:
    """Build deterministic CRG-informed scopes under ``max_bytes``."""
    sections = _diff_sections(diff_text)
    if not sections:
        return _unparsed_scopes(diff_text, files, max_bytes, crg_analysis, graph_context)
    if len(sections) <= 1 and len(diff_text.encode()) <= max_bytes:
        return [_make_scope(diff_text, files, "scope-01", crg_analysis, graph_context)] if diff_text else [], False
    impacted = set(_analysis_files(crg_analysis, "impacted_files"))
    ordered = sorted(
        enumerate(sections),
        key=lambda item: (0 if item[1][0] in impacted else 1, item[0]),
    )
    return _pack_scopes(ordered, max_bytes, crg_analysis, graph_context)


def _pack_scopes(
    ordered: list[tuple[int, tuple[str, str]]],
    max_bytes: int,
    analysis: dict[str, object] | None,
    graph_context: dict[str, object] | None,
) -> tuple[list[DiffChunk], bool]:
    chunks: list[DiffChunk] = []
    current: list[tuple[str, str]] = []
    current_bytes = 0
    truncated = False
    for _, (path, section) in ordered:
        encoded = section.encode()
        if len(encoded) > max_bytes:
            _flush_scope(chunks, current, analysis, graph_context)
            current = []
            current_bytes = 0
            chunks.append(_truncated_scope(path, encoded, max_bytes, len(chunks) + 1, analysis, graph_context))
            truncated = True
            continue
        if current and current_bytes + len(encoded) > max_bytes:
            _flush_scope(chunks, current, analysis, graph_context)
            current = []
            current_bytes = 0
        current.append((path, section))
        current_bytes += len(encoded)
    _flush_scope(chunks, current, analysis, graph_context)
    return chunks, truncated
def _unparsed_scopes(
    diff_text: str,
    files: list[str],
    max_bytes: int,
    analysis: dict[str, object] | None,
    graph_context: dict[str, object] | None,
) -> tuple[list[DiffChunk], bool]:
    encoded = diff_text.encode()
    if len(encoded) <= max_bytes:
        return ([_make_scope(diff_text, files, "scope-01", analysis, graph_context)] if diff_text else [], False)
    return ([_truncated_scope("(unparsed diff)", encoded, max_bytes, 1, analysis, graph_context)], True)



def _make_scope(
    diff_text: str,
    files: list[str],
    scope_id: str,
    analysis: dict[str, object] | None,
    graph_context: dict[str, object] | None,
) -> DiffChunk:
    return DiffChunk(
        diff_text,
        "\n".join(files) + ("\n" if files else ""),
        scope_id=scope_id,
        context_files=_crg_context_files(files, analysis),
        affected_flows=_affected_flows(analysis),
        bridge_files=_bridge_files(graph_context),
    )


def _flush_scope(
    chunks: list[DiffChunk],
    current: list[tuple[str, str]],
    analysis: dict[str, object] | None,
    graph_context: dict[str, object] | None,
) -> None:
    if not current:
        return
    primary = [path for path, _ in current]
    chunks.append(_make_scope("".join(text for _, text in current), primary, f"scope-{len(chunks) + 1:02d}", analysis, graph_context))


def _truncated_scope(
    path: str,
    encoded: bytes,
    max_bytes: int,
    index: int,
    analysis: dict[str, object] | None,
    graph_context: dict[str, object] | None,
) -> DiffChunk:
    clipped = encoded[:max_bytes].decode(errors="ignore")
    return DiffChunk(
        clipped + f"\n\n[FILE DIFF TRUNCATED: {path} original size {len(encoded)} bytes, cap {max_bytes} bytes]\n",
        path + "\n",
        True,
        f"scope-{index:02d}",
        tuple(item for item in _crg_context_files([path], analysis) if item != path),
        _affected_flows(analysis),
        _bridge_files(graph_context),
    )

def _diff_sections(diff_text: str) -> list[tuple[str, str]]:
    """Return ``(repo-relative-file, unified-diff-section)`` pairs."""
    return [
        (path, section)
        for raw in diff_text.split("diff --git ")
        if raw.strip()
        for section, path in [_parse_diff_section(raw)]
        if path
    ]


def _parse_diff_section(raw: str) -> tuple[str, str]:
    section = "diff --git " + raw
    lines = raw.splitlines()
    path = _header_path(lines[0] if lines else "")
    for line in lines:
        if line.startswith("+++ b/"):
            path = line[6:].strip()
            break
    return section, path


def _header_path(line: str) -> str:
    parts = line.split()
    return parts[1][2:] if len(parts) >= 2 and parts[1].startswith("b/") else ""


def _analysis_file_path(item: object) -> str | None:
    if isinstance(item, dict):
        path = item.get("file_path") or item.get("file")
        return str(path) if path else None
    return str(item) if item else None


def _analysis_files(analysis: dict[str, object] | None, key: str) -> list[str]:
    values = analysis.get(key, []) if isinstance(analysis, dict) else []
    paths = [_analysis_file_path(item) for item in values]
    return sorted(path for path in paths if path)

def _crg_context_files(files: list[str], analysis: dict[str, object] | None) -> tuple[str, ...]:
    impacted = _analysis_files(analysis, "impacted_files")
    return tuple(path for path in impacted if path not in files)[:20]


def _affected_flows(analysis: dict[str, object] | None) -> tuple[str, ...]:
    values = analysis.get("affected_flows", []) if isinstance(analysis, dict) else []
    return tuple(str(value) for value in values if value)[:15]


def _bridge_files(graph_context: dict[str, object] | None) -> tuple[str, ...]:
    architecture = graph_context.get("architecture", {}) if isinstance(graph_context, dict) else {}
    values = architecture.get("bridges_touched", []) if isinstance(architecture, dict) else []
    return tuple(
        str(item.get("file_path") or item.get("file"))
        for item in values
        if isinstance(item, dict) and (item.get("file_path") or item.get("file"))
    )[:15]


def scope_document(
    scopes: list[DiffChunk],
    *,
    crg_used: bool,
    max_bytes: int,
) -> dict[str, object]:
    """Return the additive deterministic scope artifact document."""
    return {
        "planner": "crg-aware-v1",
        "crg_used": crg_used,
        "max_bytes": max_bytes,
        "scope_count": len(scopes),
        "scopes": [
            {
                "id": getattr(scope, "scope_id", "") or f"scope-{index:02d}",
                "primary_files": scope.files_text.splitlines(),
                "context_files": list(getattr(scope, "context_files", ())),
                "affected_flows": list(getattr(scope, "affected_flows", ())),
                "bridge_files": list(getattr(scope, "bridge_files", ())),
                "estimated_bytes": len(scope.diff_text.encode()),
                "truncated": scope.truncated,
            }
            for index, scope in enumerate(scopes, 1)
        ],
    }


__all__ = ["DiffChunk", "build_chunks", "build_scopes", "scope_document"]
