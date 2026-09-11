"""CRG-specific single-Pi prompt sections."""
from __future__ import annotations

from pathlib import Path
from typing import Any, Callable

_CRG_MAX_PRIORITIES = 5
_CRG_MAX_FUNCTIONS = 15
_CRG_MAX_PATHS = 30
_CRG_MAX_TEST_GAPS = 15


def _crg_entries(value: Any) -> list[dict[str, Any]]:
    """Return only dict entries; malformed CRG items are dropped."""
    return [item for item in value or [] if isinstance(item, dict)]


def _crg_risk(item: dict[str, Any]) -> float:
    """Best-effort numeric risk; malformed scores sort as zero."""
    try:
        return float(item.get("risk_score", 0) or 0)
    except (TypeError, ValueError):
        return 0.0


def _crg_entry_sort_key(item: dict[str, Any]) -> tuple:
    """Deterministic ordering: risk descending, then path/name ascending."""
    name = item.get("qualified_name") or item.get("name") or ""
    file = item.get("file") or item.get("file_path") or ""
    return (-_crg_risk(item), str(file), str(name))


def _crg_renderers(
    render_section: Callable[..., str] | None,
    byte_cap_with_pointer: Callable[..., str] | None,
) -> tuple[Callable[..., str], Callable[..., str]]:
    if render_section is None or byte_cap_with_pointer is None:
        from ...reasoning.single_pi import _byte_cap_with_pointer, render_section as _render_section
        render_section = _render_section
        byte_cap_with_pointer = _byte_cap_with_pointer
    return render_section, byte_cap_with_pointer


def _crg_pointer(context_dir: Path | None, key: str) -> str | None:
    return f".reviewforge-context/graph-context.json (key: {key})" if context_dir else None


def _crg_header(analysis: dict[str, Any], status: str) -> list[str]:
    lines = []
    if analysis.get("summary"):
        lines.append(str(analysis["summary"]).strip())
    if status == "degraded":
        lines.append("Note: analysis was truncated at the tool's function cap; results are partial.")
    try:
        lines.append(f"Overall risk score: {float(analysis['risk_score']):.2f}")
    except (KeyError, TypeError, ValueError):
        pass
    return lines


def _crg_ranked_sections(
    analysis: dict[str, Any],
    render_section: Callable[..., str],
    context_dir: Path | None,
) -> list[str]:
    specs = (
        ("review_priorities", "Review priorities (highest risk first):", _CRG_MAX_PRIORITIES,
         lambda item: f"  - {item.get('qualified_name') or item.get('name', '?')} (risk={_crg_risk(item):.2f})"),
        ("changed_functions", "Changed functions (highest risk first):", _CRG_MAX_FUNCTIONS,
         lambda item: f"  - {item.get('qualified_name') or item.get('name', '?')} ({item.get('file') or item.get('file_path') or '?'}, risk={_crg_risk(item):.2f})"),
        ("test_gaps", "Functions without test coverage:", _CRG_MAX_TEST_GAPS,
         lambda item: f"  - {item.get('qualified_name') or item.get('name', '?')}"),
    )
    sections = []
    for key, title, limit, format_item in specs:
        items = sorted(_crg_entries(analysis.get(key)), key=_crg_entry_sort_key)
        if items:
            sections.extend(
                render_section(title, [format_item(item) for item in items], limit, _crg_pointer(context_dir, key)).splitlines()
            )
    return sections


def _crg_extra_sections(
    analysis: dict[str, Any],
    render_section: Callable[..., str],
    context_dir: Path | None,
) -> list[str]:
    lines = []
    impacted = sorted(str(path) for path in (analysis.get("impacted_files") or []))
    if impacted:
        lines.extend(render_section("Impacted files:", [f"  - {path}" for path in impacted], _CRG_MAX_PATHS, _crg_pointer(context_dir, "impacted_files")).splitlines())
    affected = [str(flow) for flow in (analysis.get("affected_flows") or [])]
    if affected and len(affected) <= _CRG_MAX_PRIORITIES:
        lines.append(f"Affected flows: {', '.join(affected)}")
    elif affected:
        lines.extend(render_section("Affected flows:", [f"  - {flow}" for flow in affected], _CRG_MAX_PRIORITIES, _crg_pointer(context_dir, "affected_flows")).splitlines())
    return lines


def build_crg_section(
    analysis: dict[str, Any],
    max_bytes: int,
    context_dir: Path | None = None,
    render_section: Callable[..., str] | None = None,
    byte_cap_with_pointer: Callable[..., str] | None = None,
) -> str:
    """Format the complete CRG analysis as a bounded progressive summary."""
    render_section, byte_cap_with_pointer = _crg_renderers(render_section, byte_cap_with_pointer)
    if not isinstance(analysis, dict) or not analysis or max_bytes <= 0:
        return ""
    status = analysis.get("status") or analysis.get("crg_status")
    if status not in {"ok", "degraded"}:
        return ""
    lines = _crg_header(analysis, status)
    lines.extend(_crg_ranked_sections(analysis, render_section, context_dir))
    lines.extend(_crg_extra_sections(analysis, render_section, context_dir))
    return byte_cap_with_pointer("\n".join(lines), max_bytes, _crg_pointer(context_dir, "full"))


def _wave2_api(context: dict[str, Any], render_section: Callable[..., str], context_dir: Path | None) -> str:
    api = context.get("api_surface")
    if not isinstance(api, dict) or api.get("status") not in {"ok", "degraded"}:
        return ""
    pointer = _crg_pointer(context_dir, "api_surface")
    lines = ["Deterministic API-surface changes:"]
    specs = (
        ("breaking_candidates", lambda item: f"  - {item.get('symbol', '?')} (callers={item.get('caller_count', 0)})", 15),
        ("added_nodes", lambda name: f"  - added {name}", 10),
        ("removed_nodes", lambda name: f"  - removed {name}", 10),
    )
    for key, formatter, limit in specs:
        values = api.get(key) if isinstance(api.get(key), list) else []
        values = [item for item in values if key == "breaking_candidates" and isinstance(item, dict) or key != "breaking_candidates"]
        lines.extend(render_section("", [formatter(item) for item in values], limit, _crg_pointer(context_dir, f"api_surface.{key}")).splitlines())
    return "\n".join(lines)


def _wave2_flows(context: dict[str, Any], render_section: Callable[..., str], context_dir: Path | None) -> str:
    flow = context.get("flows")
    if not isinstance(flow, dict):
        return ""
    lines = []
    for item in flow.get("top") if isinstance(flow.get("top"), list) else []:
        if not isinstance(item, dict):
            continue
        try:
            criticality = float(item.get("criticality", 0) or 0)
        except (TypeError, ValueError):
            criticality = 0.0
        lines.append(f"  - {item.get('entry_point', '?')} (criticality={criticality:.3f})")
    return "\n".join(["Critical flows reached by this change:", render_section("", lines, 15, _crg_pointer(context_dir, "flows.top"))]) if lines else ""


def _wave2_architecture(context: dict[str, Any], render_section: Callable[..., str], context_dir: Path | None) -> str:
    arch = context.get("architecture")
    if not isinstance(arch, dict):
        return ""
    values = {
        "hubs_touched": [str(item.get("qualified_name", "?")) for item in arch.get("hubs_touched", []) if isinstance(item, dict)],
        "bridges_touched": [str(item.get("qualified_name", "?")) for item in arch.get("bridges_touched", []) if isinstance(item, dict)],
    }
    crossed = arch.get("communities_crossed", 0)
    if not any(values.values()) and not crossed:
        return ""
    lines = ["Architecture facts:"]
    for key, label in (("hubs_touched", "hubs"), ("bridges_touched", "bridges")):
        items = values[key]
        lines.append(f"  - {label}: {', '.join(items)}" if len(items) <= 15 else render_section(f"  - {label}:", items, 15, _crg_pointer(context_dir, f"architecture.{key}")))
    lines.append(f"  - community boundaries crossed: {crossed}")
    return "\n".join(lines)
def build_wave2_section(
    context: dict[str, Any],
    max_bytes: int,
    context_dir: Path | None = None,
    render_section: Callable[..., str] | None = None,
    byte_cap_with_pointer: Callable[..., str] | None = None,
) -> str:
    """Format wave-two graph context into progressive-disclosure sections."""
    render_section, byte_cap_with_pointer = _crg_renderers(render_section, byte_cap_with_pointer)
    sections = [
        section for section in (
            _wave2_api(context, render_section, context_dir),
            _wave2_flows(context, render_section, context_dir),
            _wave2_architecture(context, render_section, context_dir),
        ) if section
    ]
    return byte_cap_with_pointer(
        "\n".join(sections),
        max_bytes,
        _crg_pointer(context_dir, "wave2"),
    )
