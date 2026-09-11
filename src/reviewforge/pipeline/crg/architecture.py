"""CRG architecture analysis."""
from __future__ import annotations

from typing import Any


def _changed_path(item: dict[str, Any]) -> str:
    return str(item.get("file") or item.get("file_path") or "")


def _changed_node_names(store: Any, changed: set[str]) -> set[str]:
    return {
        node.qualified_name
        for node in store.get_all_nodes(exclude_files=True)
        if any(str(node.file_path).endswith(file) for file in changed)
    }


def _related_nodes(store: Any, changed_nodes: set[str]) -> set[str]:
    related = set(changed_nodes)
    for edge in store.get_all_edges():
        if edge.target_qualified in changed_nodes and edge.kind == "CALLS":
            related.add(edge.source_qualified)
    return related


def architecture(store: Any, changed_files: list[str]) -> dict[str, Any]:
    from code_review_graph.analysis import find_bridge_nodes, find_hub_nodes
    changed = set(changed_files)
    hubs = [item for item in find_hub_nodes(store, 50) if any(_changed_path(item).endswith(file) for file in changed)]
    bridges = [item for item in find_bridge_nodes(store, 50) if any(_changed_path(item).endswith(file) for file in changed)]
    communities = store.get_all_community_ids()
    changed_nodes = _changed_node_names(store, changed)
    community_ids = {communities.get(name) for name in _related_nodes(store, changed_nodes)} - {None}
    labels = {str(value): "community-" + str(value) for value in sorted(community_ids)}
    return {
        "status": "ok",
        "hubs_touched": sorted(hubs, key=lambda item: item.get("qualified_name", "")),
        "bridges_touched": sorted(bridges, key=lambda item: item.get("qualified_name", "")),
        "communities_crossed": len(community_ids),
        "community_labels": labels,
    }
