"""CRG analysis document and artifact projections."""
from __future__ import annotations

import json
from typing import Any

from ...runlog import warning as log_warning
from ..stage import StageContext


def _strip_root(value: Any, repo_root: str) -> Any:
    """Strip the disposable absolute checkout prefix from analysis strings."""
    prefix = repo_root + "/"
    if isinstance(value, str):
        return value.removeprefix(prefix) if value.startswith(prefix) else value
    if isinstance(value, list):
        return [_strip_root(item, repo_root) for item in value]
    if isinstance(value, dict):
        return {key: _strip_root(item, repo_root) for key, item in value.items()}
    return value


def _entry_file(item: dict[str, Any]) -> str | None:
    """Return an entry's file; CRG node payloads use ``file_path``."""
    path = item.get("file_path") or item.get("file")
    return str(path) if path else None


def _analysis_paths(items: Any) -> set[str]:
    return {
        path
        for item in items or []
        if isinstance(item, dict)
        if (path := _entry_file(item))
    }


def _impacted_files(analysis: dict[str, Any]) -> list[str]:
    """Return the sorted unique file set touched by the analysis."""
    files = set().union(*(_analysis_paths(analysis.get(key)) for key in (
        "changed_functions", "review_priorities", "test_gaps"
    )))
    return sorted(files)


def _base_document(tool_version: str | None) -> dict[str, Any]:
    return {
        "status": "failed",
        "tool_version": tool_version,
        "build": {"mode": "none", "duration_ms": 0},
        "summary": "",
        "risk_score": 0.0,
        "changed_functions": [],
        "affected_flows": [],
        "test_gaps": [],
        "impacted_files": [],
        "review_priorities": [],
    }


def _build_document(
    analysis: dict[str, Any],
    *,
    status: str,
    tool_version: str,
    build: dict[str, Any],
    repo_root: str,
) -> dict[str, Any]:
    """Compose the canonical ``crg-analysis.json`` document."""
    document = _base_document(tool_version)
    document.update(
        status=status,
        build=build,
        summary=analysis.get("summary", ""),
        risk_score=analysis.get("risk_score", 0.0),
        changed_functions=analysis.get("changed_functions", []),
        affected_flows=analysis.get("affected_flows", []),
        test_gaps=analysis.get("test_gaps", []),
        impacted_files=_impacted_files(analysis),
        review_priorities=analysis.get("review_priorities", []),
    )
    if analysis.get("functions_truncated"):
        document["functions_truncated"] = True
    return _strip_root(document, repo_root)


def _write_failure_document(ctx: StageContext, *, tool_version: str | None, error: str) -> None:
    """Best-effort ``status: "failed"`` artifact so operators can see the miss."""
    document = _base_document(tool_version)
    document["error"] = error
    try:
        _write_artifact(ctx, document)
    except Exception as exc:  # noqa: BLE001
        log_warning(f"CRG failure artifact write failed ({type(exc).__name__}: {exc})")
    try:
        ctx.artifacts.graph_context.unlink(missing_ok=True)
    except Exception as exc:  # noqa: BLE001
        log_warning(f"CRG graph-context unlink failed ({type(exc).__name__}: {exc})")
    try:
        _write_graph_context(ctx, document)
    except Exception as exc:  # noqa: BLE001
        log_warning(f"Graph-context failure artifact write failed ({type(exc).__name__}: {exc})")


def _write_artifact(ctx: StageContext, document: dict[str, Any]) -> None:
    """Serialise ``document`` to the ``crg-analysis.json`` artifact."""
    ctx.artifacts.crg_analysis.write_text(
        json.dumps(document, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def _write_graph_context(ctx: StageContext, document: dict[str, Any]) -> None:
    """Write the additive graph-context projection."""
    ctx.artifacts.graph_context.write_text(
        json.dumps(document, ensure_ascii=False, indent=2, sort_keys=True),
        encoding="utf-8",
    )
