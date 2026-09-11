"""Deterministic, best-effort CRG snapshot analyses."""
from __future__ import annotations

import json
import shutil
import tempfile
from pathlib import Path
from typing import Any


def snapshot(store: Any) -> dict[str, Any]:
    from code_review_graph.graph_diff import take_snapshot
    data = take_snapshot(store)
    edges = data.get("edges", {})
    if isinstance(edges, set):
        data["edges"] = {edge: True for edge in sorted(edges)}
    return data


def diff_snapshots(base: dict[str, Any], source: dict[str, Any]) -> dict[str, Any]:
    bn, sn = base.get("nodes", {}), source.get("nodes", {})
    be, se = base.get("edges", {}), source.get("edges", {})
    added = sorted(set(sn) - set(bn))
    removed = sorted(set(bn) - set(sn))
    changed = sorted(name for name in set(bn) & set(sn) if bn[name] != sn[name])
    added_edges = sorted(set(se) - set(be))
    removed_edges = sorted(set(be) - set(se))
    return {
        "added_nodes": added[:50],
        "removed_nodes": removed[:50],
        "changed_nodes": changed[:50],
        "added_edges": added_edges[:50],
        "removed_edges": removed_edges[:50],
        "truncated": any(
            len(values) > 50
            for values in (added, removed, changed, added_edges, removed_edges)
        ),
    }


def _watched_nodes(base: dict[str, Any], source: dict[str, Any]) -> set[str]:
    base_nodes, source_nodes = base.get("nodes", {}), source.get("nodes", {})
    return (
        set(base_nodes) - set(source_nodes)
        | {
            name for name in set(base_nodes) & set(source_nodes)
            if base_nodes[name] != source_nodes[name]
        }
    )


def _breaking_candidates(source: dict[str, Any], watched: set[str]) -> list[dict[str, Any]]:
    candidates: dict[str, dict[str, Any]] = {}
    for edge in source.get("edges", {}):
        parts = edge.split(":", 1)
        if len(parts) != 2 or parts[1] != "CALLS":
            continue
        endpoints = parts[0].split("->", 1)
        if len(endpoints) != 2 or endpoints[1] not in watched:
            continue
        candidate = candidates.setdefault(
            endpoints[1],
            {"symbol": endpoints[1], "reason": "removed or changed node with surviving incoming call edges", "caller_count": 0},
        )
        candidate["caller_count"] += 1
    return sorted(candidates.values(), key=lambda item: (item["symbol"], item["reason"]))
def api_surface(base: dict[str, Any], source: dict[str, Any], changed_files: list[str]) -> dict[str, Any]:
    del changed_files
    result = diff_snapshots(base, source)
    candidates = _breaking_candidates(source, _watched_nodes(base, source))
    if len(candidates) > 50:
        result["truncated"] = True
    result["breaking_candidates"] = candidates[:50]
    return result


def build_base_snapshot(ctx: Any, tool_version: str) -> dict[str, Any]:
    from ...git.ops import run_git
    from code_review_graph.graph import GraphStore
    import code_review_graph.incremental as inc
    from ...runlog import info

    base = getattr(ctx.state, "base_commit", "")
    if not base:
        raise ValueError("base commit unavailable")
    root = Path(
        getattr(ctx.cfg, "crg_cache_dir", None)
        or ctx.cfg.review_artifact_root / "crg-cache"
    )
    path = root / _safe(getattr(ctx.cfg, "ado_repo_id", "default")) / "base-snapshots" / f"crg-{tool_version}" / f"{base}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        info(f"CRG base snapshot reused: {path}")
        return json.loads(path.read_text(encoding="utf-8"))

    worktree = Path(tempfile.mkdtemp(prefix="crg-base-"))
    graph_data_dir = Path(tempfile.mkdtemp(prefix="crg-base-graph-"))
    cleanup_paths = getattr(ctx.state, "cleanup_paths", None)
    if isinstance(cleanup_paths, list):
        for temp_path in (worktree, graph_data_dir):
            if temp_path not in cleanup_paths:
                cleanup_paths.append(temp_path)
    try:
        run_git(ctx.state.repo_dir, "worktree", "add", "--detach", str(worktree), base)
        store = GraphStore(graph_data_dir / "base.db")
        inc.full_build(worktree, store)
        data = snapshot(store)
        path.write_text(json.dumps(data, sort_keys=True), encoding="utf-8")
        info(f"CRG base snapshot built: {path}")
        return data
    finally:
        try:
            run_git(ctx.state.repo_dir, "worktree", "remove", "--force", str(worktree), check=False)
        except Exception:
            pass
        shutil.rmtree(worktree, ignore_errors=True)
        shutil.rmtree(graph_data_dir, ignore_errors=True)


def _safe(value: str) -> str:
    return "".join(c if c.isalnum() or c in "_-" else "_" for c in value) or "default"
