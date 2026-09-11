"""Stage: enrich context with a Tree-sitter code-review graph (CRG).

Runs between :class:`PrepareRepositoryStage` and
:class:`ExecuteReasoningEngineStage`. Uses the optional ``code-review-graph``
PyPI package to build a lightweight SQLite knowledge graph from the
checked-out working tree, then analyses which functions were changed, their
blast radius, affected flows, test gaps, and risk scores.

The result is stored in ``ctx.extras["crg_analysis"]`` so that
:func:`~reviewforge.reasoning.single_pi._build_single_pi_prefix` can
forward it as structured context to the model. It is also written as
``crg-analysis.json`` in the run artifact directory.

The graph DB is stored at
``<cache-root>/<repo_id>/crg-<tool_version>/crg.db`` and **persisted
across runs**. The cache root defaults to
``<review_artifact_root>/crg-cache`` (inside the artifact volume) and can
be redirected with ``CRG_CACHE_DIR`` (e.g. a dedicated cache volume). The
tool version is part of the path, so a CRG upgrade triggers exactly one
cold rebuild instead of reusing an incompatible graph.

On the first run for a repository (no DB file yet) a full build is
performed (~10 s per 500 files). On subsequent runs an incremental update
(<2 s, SHA-256-based) re-parses only the PR's changed files and their
dependents; the changed-file list comes from the pipeline, not from a git
base guess, so shallow checkouts cannot silently degrade the warm path.
The stage falls back to a full build if ``incremental_update`` is absent
or raises.

This stage MUST NOT raise on any CRG failure. Any error degrades
gracefully to today's behavior: a warning is logged, ``crg-analysis.json``
is written with ``status: "failed"``, and the pipeline continues.
"""
from __future__ import annotations

import re
import time
from importlib import metadata as importlib_metadata
from pathlib import Path
from typing import Any

from ...runlog import info as _log, warning as log_warning
from ..context_staging import stage_context_files
from ..stage import Stage, StageContext
from .analysis import (
    _build_document,
    _write_artifact,
    _write_failure_document,
    _write_graph_context,
)

_CRG_DISTRIBUTION = "code-review-graph"


def _load_crg_modules() -> tuple[Any, Any, Any, Any] | None:
    try:
        from code_review_graph.graph import GraphStore
        import code_review_graph.incremental as crg_inc
        from code_review_graph.changes import analyze_changes, parse_git_diff_ranges
    except ImportError:
        return None
    return GraphStore, crg_inc, analyze_changes, parse_git_diff_ranges


def _build_graph(
    ctx: StageContext,
    repo_dir: Path,
    changed_files: list[str],
    graph_store: Any,
    crg_inc: Any,
    tool_version: str,
) -> tuple[Any, str, int, int]:
    db_path = _crg_db_path(ctx, tool_version)
    db_existed = db_path.exists()
    store = graph_store(db_path)
    started = time.monotonic()
    incremental = getattr(crg_inc, "incremental_update", None)
    if db_existed and incremental is not None:
        try:
            result = incremental(repo_dir, store, changed_files=changed_files)
            mode = "incremental"
        except Exception as exc:
            log_warning(f"CRG incremental update failed ({type(exc).__name__}: {exc}); falling back to full build")
            result = crg_inc.full_build(repo_dir, store)
            mode = "full"
    else:
        result = crg_inc.full_build(repo_dir, store)
        mode = "full"
    duration = int((time.monotonic() - started) * 1000)
    nodes = result.get("nodes", result.get("total_nodes", 0)) if isinstance(result, dict) else 0
    _log(f"CRG graph {mode} build: {nodes} nodes from {repo_dir}")
    return store, mode, duration, nodes


def _analyze_graph(
    store: Any,
    repo_dir: Path,
    changed_files: list[str],
    range_spec: str,
    analyze_changes: Any,
    parse_git_diff_ranges: Any,
) -> dict[str, Any]:
    root = str(repo_dir)
    ranges = parse_git_diff_ranges(root, range_spec) if range_spec else None
    changed_ranges = {str(repo_dir / key): value for key, value in ranges.items()} if ranges is not None else None
    absolute_files = [str(repo_dir / path) if not Path(path).is_absolute() else path for path in changed_files]
    analysis = analyze_changes(store, absolute_files, changed_ranges=changed_ranges, repo_root=root)
    if not isinstance(analysis, dict):
        raise ValueError("CRG analysis returned a non-object payload")
    return analysis


def _optional_analysis(
    ctx: StageContext,
    store: Any,
    changed_files: list[str],
    tool_version: str,
) -> tuple[dict[str, Any], dict[str, int]]:
    graph_context: dict[str, Any] = {}
    details: dict[str, int] = {}
    analyses = (
        ("graph_api_diff", "graph_api_diff_ms", "api_surface", "CRG API-surface analysis degraded"),
        ("graph_flows", "graph_flows_ms", "flows", "CRG flow analysis degraded"),
        ("graph_arch", "graph_arch_ms", "architecture", "CRG architecture analysis degraded"),
    )
    for flag, metric, key, message in analyses:
        if not getattr(ctx.cfg, flag, False):
            continue
        started = time.monotonic()
        try:
            value = _run_analysis(key, ctx, store, changed_files, tool_version)
        except Exception as exc:
            log_warning(f"{message} ({type(exc).__name__}: {exc})")
            value = _degraded_value(key, ctx, exc)
        graph_context[key] = value
        details[metric] = int((time.monotonic() - started) * 1000)
    return graph_context, details


def _run_analysis(
    key: str,
    ctx: StageContext,
    store: Any,
    changed_files: list[str],
    tool_version: str,
) -> dict[str, Any]:
    if key == "api_surface":
        from .snapshots import api_surface, build_base_snapshot, snapshot
        base = build_base_snapshot(ctx, tool_version)
        return {"status": "ok", "base_commit": getattr(ctx.state, "base_commit", ""), **api_surface(base, snapshot(store), changed_files)}
    if key == "flows":
        from .flows import flows
        return flows(store, changed_files)
    from .architecture import architecture
    return architecture(store, changed_files)


def _degraded_value(key: str, ctx: StageContext, exc: Exception) -> dict[str, Any]:
    base_commit = getattr(ctx.state, "base_commit", "")
    if key == "api_surface":
        return {"status": "degraded", "base_commit": base_commit, "added_nodes": [], "removed_nodes": [], "changed_nodes": [], "added_edges": [], "removed_edges": [], "truncated": False, "breaking_candidates": [], "error": str(exc)}
    if key == "flows":
        return {"status": "degraded", "affected_count": 0, "top": [], "error": str(exc)}
    return {"status": "degraded", "hubs_touched": [], "bridges_touched": [], "communities_crossed": 0, "community_labels": {}, "error": str(exc)}
class EnrichWithCrgStage(Stage):
    """Build a Tree-sitter knowledge graph and analyse the PR diff."""

    name = "enrich_with_crg"

    def should_run(self, ctx: StageContext) -> bool:
        if not ctx.cfg.crg_enabled:
            return False
        if getattr(ctx.extras.get("review_state"), "mode", None) == "no_op":
            return False
        if ctx.state is None or not getattr(ctx.state, "repo_dir", None):
            return False
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        repo_dir: Path = ctx.state.repo_dir
        changed_files = list(getattr(ctx.state, "files", []))
        range_spec = getattr(ctx.state, "range_spec", "")
        modules = _load_crg_modules()
        if modules is None:
            log_warning(
                "CRG enrichment skipped: 'code-review-graph' package is not installed. "
                "Install it with: pip install code-review-graph"
            )
            _write_failure_document(ctx, tool_version=None, error="package unavailable")
            return {"crg_status": "package_unavailable"}
        graph_store, crg_inc, analyze_changes, parse_git_diff_ranges = modules
        tool_version = _crg_version()
        if tool_version == "unknown":
            # Modules import but the distribution metadata is missing: broken
            # install — disable CRG instead of cache-keying under "unknown".
            log_warning("CRG enrichment skipped: 'code-review-graph' distribution metadata is missing.")
            _write_failure_document(ctx, tool_version=None, error="package metadata unavailable")
            return {"crg_status": "package_unavailable"}
        try:
            store, build_mode, build_duration_ms, node_count = _build_graph(
                ctx, repo_dir, changed_files, graph_store, crg_inc, tool_version
            )
        except Exception as exc:
            log_warning(f"CRG graph build failed ({type(exc).__name__}: {exc}); skipping enrichment")
            _write_failure_document(ctx, tool_version=tool_version, error=str(exc))
            stage_context_files(ctx)
            return {"crg_status": "build_failed", "crg_error": str(exc)}
        try:
            analysis = _analyze_graph(
                store, repo_dir, changed_files, range_spec, analyze_changes, parse_git_diff_ranges
            )
        except Exception as exc:
            log_warning(f"CRG analysis failed ({type(exc).__name__}: {exc}); skipping enrichment")
            _write_failure_document(ctx, tool_version=tool_version, error=str(exc))
            stage_context_files(ctx)
            return {"crg_status": "analysis_failed", "crg_error": str(exc)}
        status = "degraded" if analysis.get("functions_truncated") else "ok"
        document = _build_document(
            analysis, status=status, tool_version=tool_version,
            build={"mode": build_mode, "duration_ms": build_duration_ms},
            repo_root=str(repo_dir),
        )
        graph_context, details = _optional_analysis(ctx, store, changed_files, tool_version)
        graph_context = {**document, **graph_context}
        for writer, label, value in (
            (_write_artifact, "CRG artifact", document),
            (_write_graph_context, "Graph-context artifact", graph_context),
        ):
            try:
                writer(ctx, value)
            except Exception as exc:
                log_warning(f"{label} write failed ({type(exc).__name__}: {exc}); continuing without artifact")
        stage_context_files(ctx)
        ctx.extras["crg_analysis"] = document
        ctx.extras["graph_context"] = graph_context
        _log(
            f"CRG enrichment complete: risk_score={document['risk_score']:.2f}, "
            f"changed_functions={len(document['changed_functions'])}, "
            f"test_gaps={len(document['test_gaps'])}"
        )
        return {
            "crg_status": status,
            "crg_build_mode": build_mode,
            "crg_nodes": node_count,
            "crg_risk_score": document["risk_score"],
            "crg_changed_functions": len(document["changed_functions"]),
            "crg_test_gaps": len(document["test_gaps"]),
            **details,
        }


def _crg_version() -> str:
    """Return the installed ``code-review-graph`` version (``unknown`` fallback)."""
    try:
        return importlib_metadata.version(_CRG_DISTRIBUTION)
    except importlib_metadata.PackageNotFoundError:
        return "unknown"


def _crg_db_path(ctx: StageContext, tool_version: str) -> Path:
    """Return the persistent per-repo SQLite path for the CRG graph.

    Stored at ``<cache-root>/<repo_id>/crg-<tool_version>/crg.db`` and
    shared across all runs for the same repository. The cache root is
    ``CRG_CACHE_DIR`` when configured, otherwise
    ``<review_artifact_root>/crg-cache`` — both live on persistent volumes,
    never under the per-run artifact dir or the disposable repo checkout.
    The tool version in the path guarantees a cold rebuild after a CRG
    upgrade. The directory is created eagerly so that ``GraphStore`` can
    open the file on first use.
    """
    repo_id = _safe_repo_id(ctx.cfg.ado_repo_id or "default")
    override = getattr(ctx.cfg, "crg_cache_dir", None)
    root = Path(override) if override else ctx.cfg.review_artifact_root / "crg-cache"
    cache_dir = root / repo_id / f"crg-{tool_version}"
    cache_dir.mkdir(parents=True, exist_ok=True)
    return cache_dir / "crg.db"


def _safe_repo_id(repo_id: str) -> str:
    """Sanitise *repo_id* so it is safe to use as a directory name."""
    sanitised = re.sub(r"[^\w-]", "_", repo_id)
    return sanitised or "default"


__all__ = ["EnrichWithCrgStage"]
