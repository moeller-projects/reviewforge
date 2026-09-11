"""Stage complete deterministic context files inside the reviewed checkout."""
from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from ..runlog import SECRET_NAMES, warning

_CONTEXT_DIR = ".reviewforge-context"

_FILE_DESCRIPTIONS = {
    "metadata.json": "Pull request and repository metadata",
    "commits.txt": "Commit subjects in the reviewed range",
    "changed-files.json": "Files changed by the reviewed range",
    "work-items.json": "Linked Azure DevOps work items",
    "threads.json": "Existing pull request comment threads",
    "review-state.json": "Deterministic previous-review state",
    "graph-context.json": "Complete deterministic graph analysis",
}


def _secret_values(ctx: Any) -> tuple[bytes, ...]:
    values = [os.environ.get(name, "") for name in SECRET_NAMES]
    values.append(str(getattr(ctx.cfg, "ado_token", "") or ""))
    return tuple(value.encode("utf-8") for value in values if value)


def _redacted_copy(source: Path, destination: Path, secrets: tuple[bytes, ...]) -> list[str]:
    payload = source.read_bytes()
    for secret in secrets:
        payload = payload.replace(secret, b"***")
    destination.write_bytes(payload)
    if destination.suffix != ".json":
        return []
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return []
    return sorted(value) if isinstance(value, dict) else []


def _write_review_state(ctx: Any, destination: Path, secrets: tuple[bytes, ...]) -> list[str] | None:
    review_context = ctx.extras.get("review_context")
    if not isinstance(review_context, dict):
        destination.unlink(missing_ok=True)
        return None
    payload = json.dumps(review_context, ensure_ascii=False, sort_keys=True, indent=2).encode("utf-8")
    for secret in secrets:
        payload = payload.replace(secret, b"***")
    destination.write_bytes(payload + b"\n")
    return sorted(review_context)


def _context_sources(artifacts: Any, include_graph_context: bool) -> dict[str, Path]:
    sources = {
        "metadata.json": artifacts.metadata,
        "commits.txt": artifacts.commits,
        "changed-files.json": artifacts.changed_files,
        "work-items.json": artifacts.work_items,
        "threads.json": artifacts.threads,
    }
    if include_graph_context:
        sources["graph-context.json"] = artifacts.graph_context
    return sources


def _copy_context_sources(
    sources: dict[str, Path], staging_dir: Path, secrets: tuple[bytes, ...]
) -> dict[str, dict[str, Any]]:
    index = {}
    for name, source in sources.items():
        destination = staging_dir / name
        if not source.exists():
            destination.unlink(missing_ok=True)
            continue
        index[name] = {
            "description": _FILE_DESCRIPTIONS[name],
            "top_level_keys": _redacted_copy(source, destination, secrets),
        }
    return index


def stage_context_files(ctx: Any, *, include_graph_context: bool = True) -> Path | None:
    """Copy complete deterministic context files into the reviewed checkout."""
    repo_dir = getattr(getattr(ctx, "state", None), "repo_dir", None)
    if not repo_dir:
        warning("deterministic context staging skipped: repository checkout unavailable")
        ctx.extras.pop("context_staging_dir", None)
        ctx.extras.pop("context_staging_index", None)
        return None
    repo_dir = Path(repo_dir)
    staging_dir = repo_dir / _CONTEXT_DIR
    existing = ctx.extras.get("context_staging_dir")
    owned_dir = bool(existing) and Path(existing) == staging_dir
    if staging_dir.exists() and not owned_dir:
        warning("deterministic context staging skipped: checkout already contains .reviewforge-context")
        ctx.extras.pop("context_staging_dir", None)
        ctx.extras.pop("context_staging_index", None)
        return None
    staging_dir.mkdir(parents=True, exist_ok=True)
    if not include_graph_context:
        (staging_dir / "graph-context.json").unlink(missing_ok=True)
    secrets = _secret_values(ctx)
    index = _copy_context_sources(_context_sources(ctx.artifacts, include_graph_context), staging_dir, secrets)
    review_state_keys = _write_review_state(ctx, staging_dir / "review-state.json", secrets)
    if review_state_keys is not None:
        index["review-state.json"] = {
            "description": _FILE_DESCRIPTIONS["review-state.json"],
            "top_level_keys": review_state_keys,
        }
    (staging_dir / "index.json").write_text(
        json.dumps(index, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    ctx.extras["context_staging_dir"] = staging_dir
    cleanup_paths = getattr(getattr(ctx, "state", None), "cleanup_paths", None)
    if isinstance(cleanup_paths, list) and staging_dir not in cleanup_paths:
        cleanup_paths.append(staging_dir)
    ctx.extras["context_staging_index"] = index
    return staging_dir


__all__ = ["stage_context_files"]
