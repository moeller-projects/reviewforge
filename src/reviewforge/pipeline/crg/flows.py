"""CRG critical-flow analysis."""
from __future__ import annotations

from typing import Any


def _flow_metadata(flow: dict[str, Any], store: Any | None) -> dict[str, Any]:
    flow = dict(flow)
    entry_point = flow.get("entry_point") or flow.get("name")
    get_node = getattr(store, "get_node", None)
    if callable(get_node) and entry_point:
        node = get_node(str(entry_point))
        if node is not None:
            flow.setdefault("entry_file", getattr(node, "file_path", ""))
            flow.setdefault("is_test", getattr(node, "is_test", False))
            flow.setdefault("decorators", getattr(node, "extra", {}).get("decorators", ""))
    return flow


def _flow_category(entry: str, file_path: str, extra: str, is_test: bool) -> str:
    if is_test or "test" in file_path or entry.split(".")[-1].startswith("test_"):
        return "test"
    if any(token in extra for token in ("route", "app.", "fastapi", "flask", "django")):
        return "http handler"
    if any(token in entry or token in file_path for token in ("cli", "command", "main")):
        return "cli"
    if any(token in entry or token in file_path for token in ("job", "task", "worker", "schedule", "cron")):
        return "job"
    return "other"


def _classify_flow(flow: dict[str, Any]) -> str:
    entry = str(flow.get("entry_point") or flow.get("name") or "").lower()
    files = [str(path).lower() for path in flow.get("files", [])]
    file_path = str(flow.get("entry_file") or flow.get("file") or (files[0] if files else "")).lower()
    extra = str(flow.get("decorators") or flow.get("framework") or "").lower()
    return _flow_category(entry, file_path, extra, bool(flow.get("is_test")))


def _flow_kind(flow: dict[str, Any], store: Any | None = None) -> str:
    return _classify_flow(_flow_metadata(flow, store))


def _path_matches(path: str, changed_files: set[str]) -> bool:
    return any(path == changed or path.endswith("/" + changed) for changed in changed_files)


def _affected_flows(store: Any, changed_files: list[str], changed: set[str], trace_flows: Any, get_affected_flows: Any) -> list[Any]:
    result = get_affected_flows(store, changed_files)
    affected = result.get("affected_flows", []) if isinstance(result, dict) else result
    if affected:
        return affected
    return [
        flow for flow in trace_flows(store)
        if isinstance(flow, dict)
        and any(_path_matches(str(path), changed) for path in flow.get("files", []))
    ]


def _flow_rows(store: Any, affected: list[Any], adjacency: Any, compute_criticality: Any) -> list[dict[str, Any]]:
    rows = []
    for flow in affected:
        if not isinstance(flow, dict):
            continue
        try:
            score = compute_criticality(flow, adjacency)
        except Exception:
            score = flow.get("criticality", 0.0)
        rows.append({
            "entry_point": str(flow.get("entry_point") or flow.get("name") or ""),
            "kind": _flow_kind(flow, store),
            "criticality": float(score or 0.0),
            "path_summary": str(flow.get("path", "")),
        })
    return rows


def flows(store: Any, changed_files: list[str]) -> dict[str, Any]:
    from code_review_graph.flows import compute_criticality, get_affected_flows, trace_flows
    changed = set(changed_files)
    affected = _affected_flows(store, changed_files, changed, trace_flows, get_affected_flows)
    rows = _flow_rows(store, affected, store.load_flow_adjacency(), compute_criticality)
    rows.sort(key=lambda item: (-item["criticality"], item["entry_point"]))
    return {"status": "ok", "affected_count": len(rows), "top": rows[:15]}
