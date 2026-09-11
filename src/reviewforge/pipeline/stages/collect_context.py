"""Stage: collect deterministic context (files, tests, searches) from the plan."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import subprocess
from pathlib import Path
from typing import Any

from ...artifacts.builder import read_json, write_json
from ..stage import Stage, StageContext


def _safe_path(repo_dir: Path, requested: str) -> Path | None:
    if not requested:
        return None
    try:
        resolved = (repo_dir / requested).resolve()
        if not resolved.is_relative_to(repo_dir.resolve()):
            return None
    except (ValueError, OSError):
        return None
    return resolved if resolved.is_file() else None


def _read_context(item: Any, repo_dir: Path, kind: str, max_lines: int) -> dict[str, Any] | None:
    requested = item.get("path", "") if kind == "files" and isinstance(item, dict) else item
    path = _safe_path(repo_dir, str(requested))
    if not path:
        return None
    lines = path.read_text(errors="replace").splitlines()
    result = {"path": str(path.relative_to(repo_dir)), "content": "\n".join(lines[:max_lines])}
    if kind == "files":
        result.update({"reason": item.get("reason", ""), "truncated": len(lines) > max_lines})
    return result


def _run_search(item: Any, repo_dir: Path, max_matches: int) -> dict[str, Any] | None:
    if not isinstance(item, dict) or not item.get("query"):
        return None
    cp = subprocess.run(
        ["rg", "-n", "--fixed-strings", "--glob", "!.git/**", "--glob", "!node_modules/**", "--glob", "!artifacts/**", "--", str(item["query"]), "."],
        cwd=str(repo_dir), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
    )
    return {"query": item["query"], "reason": item.get("reason", ""), "matches": "\n".join(cp.stdout.decode(errors="replace").splitlines()[:max_matches])}


def _collect_plan(ctx: StageContext, plan: dict[str, Any]) -> dict[str, list[Any]]:
    repo_dir = ctx.state.repo_dir
    max_lines = ctx.cfg.context_file_max_lines
    max_matches = ctx.cfg.context_search_max_matches
    workers = max(1, min(ctx.cfg.collect_context_workers, sum(len(plan.get(key, [])) for key in ("files_to_read", "tests_to_inspect", "searches_to_run")) or 1))
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [
            ("files", pool.submit(_read_context, item, repo_dir, "files", max_lines))
            for item in plan.get("files_to_read", []) if isinstance(item, dict)
        ] + [
            ("tests", pool.submit(_read_context, item, repo_dir, "tests", max_lines))
            for item in plan.get("tests_to_inspect", [])
        ] + [
            ("searches", pool.submit(_run_search, item, repo_dir, max_matches))
            for item in plan.get("searches_to_run", [])
        ]
        result = {"files": [], "tests": [], "searches": []}
        for kind, future in futures:
            if (value := future.result()):
                result[kind].append(value)
    return result


class CollectContextStage(Stage):
    name = "collect_context"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(ctx.plan)

    def run(self, ctx: StageContext) -> dict[str, Any]:
        if ctx.state is None:
            return {"files": 0, "tests": 0, "searches": 0, "skipped": True}
        result = _collect_plan(ctx, ctx.plan or {})
        write_json(ctx.artifacts.collected, result)
        ctx.collected = result
        return {kind: len(values) for kind, values in result.items()}


__all__ = ["CollectContextStage"]
