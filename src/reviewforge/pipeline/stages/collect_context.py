"""Stage: collect deterministic context (files, tests, searches) from the plan."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import subprocess
import sys
from pathlib import Path
from typing import Any

from ...artifacts.builder import read_json, write_json
from ..stage import Stage, StageContext


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


def _safe_path(repo_dir: Path, requested: str) -> Path | None:
    """Resolve ``requested`` under ``repo_dir`` and ensure it stays inside it."""
    if not requested:
        return None
    try:
        resolved = (repo_dir / requested).resolve()
        if not resolved.is_relative_to(repo_dir.resolve()):
            return None
    except (ValueError, OSError):
        return None
    return resolved if resolved.is_file() else None


class CollectContextStage(Stage):
    name = "collect_context"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(ctx.plan)

    def run(self, ctx: StageContext) -> dict[str, Any]:
        if ctx.state is None:
            return {"files": 0, "tests": 0, "searches": 0, "skipped": True}
        plan = ctx.plan or {}
        repo_dir = ctx.state.repo_dir
        result: dict[str, Any] = {"files": [], "tests": [], "searches": []}
        # Context caps live on Config (single source of truth). The stage
        # reads them off the stage context so the orchestrator and any
        # out-of-band caller stay in sync.
        max_lines = ctx.cfg.context_file_max_lines
        max_matches = ctx.cfg.context_search_max_matches
        def read_file(item: dict[str, Any], kind: str) -> dict[str, Any] | None:
            p = _safe_path(repo_dir, str(item.get("path", "") if kind == "files" else item))
            if not p:
                return None
            lines = p.read_text(errors="replace").splitlines()
            if kind == "files":
                return {
                    "path": str(p.relative_to(repo_dir)),
                    "reason": item.get("reason", ""),
                    "truncated": len(lines) > max_lines,
                    "content": "\n".join(lines[:max_lines]),
                }
            return {
                "path": str(p.relative_to(repo_dir)),
                "content": "\n".join(lines[:max_lines]),
            }

        def run_search(item: dict[str, Any]) -> dict[str, Any] | None:
            if not isinstance(item, dict) or not item.get("query"):
                return None
            cp = subprocess.run(
                [
                    "rg", "-n", "--fixed-strings",
                    "--glob", "!.git/**", "--glob", "!node_modules/**", "--glob", "!artifacts/**",
                    "--", str(item["query"]), ".",
                ],
                cwd=str(repo_dir),
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
            )
            return {
                "query": item["query"],
                "reason": item.get("reason", ""),
                "matches": "\n".join(
                    cp.stdout.decode(errors="replace").splitlines()[:max_matches]
                ),
            }

        max_workers = max(1, min(ctx.cfg.collect_context_workers, len(plan.get("files_to_read", [])) + len(plan.get("tests_to_inspect", [])) + len(plan.get("searches_to_run", [])) or 1))
        with ThreadPoolExecutor(max_workers=max_workers) as pool:
            files_futures = [pool.submit(read_file, item, "files") for item in plan.get("files_to_read", []) if isinstance(item, dict)]
            test_futures = [pool.submit(read_file, hint, "tests") for hint in plan.get("tests_to_inspect", [])]
            search_futures = [pool.submit(run_search, item) for item in plan.get("searches_to_run", [])]
            for fut in files_futures:
                item = fut.result()
                if item:
                    result["files"].append(item)
            for fut in test_futures:
                item = fut.result()
                if item:
                    result["tests"].append(item)
            for fut in search_futures:
                item = fut.result()
                if item:
                    result["searches"].append(item)
        write_json(ctx.artifacts.collected, result)
        ctx.collected = result
        return {
            "files": len(result["files"]),
            "tests": len(result["tests"]),
            "searches": len(result["searches"]),
        }


__all__ = ["CollectContextStage"]
