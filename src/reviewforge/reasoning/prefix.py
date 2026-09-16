"""Shared prompt-prefix construction for reasoning engines.

The prefix is the non-diff portion of the review user message: staging
index, metadata, changed files, commits, context items, review state, and
graph context. Extracted from ``single_pi`` so both the Pi-based engine and
the native in-process engine build byte-identical context sections.

The default intro lines reproduce the historical single-pi wording exactly;
other engines pass their own ``intro`` pair.
"""
from __future__ import annotations

import json
from typing import Any

from ..artifacts.builder import read_json
from ..git import ops as git_ops
from ..pipeline.crg.prompt import build_crg_section, build_wave2_section
from ..pipeline.stage import StageContext

_CONTEXT_MAX_FILES = 50
_CONTEXT_MAX_ITEMS = 25
_CONTEXT_MAX_REVIEW_ITEMS = 25

def render_section(title: str, items: list[Any], max_items: int, pointer: str | None) -> str:
    """Render a bounded section and point at omitted source data when available."""
    values = list(items or [])
    shown = values[:max_items] if max_items > 0 else []
    if all(isinstance(item, str) for item in values):
        body = "\n".join(str(item) for item in shown)
    else:
        body = json.dumps(shown, ensure_ascii=False)
    rendered = f"{title}\n{body}" if body else title
    omitted = max(0, len(values) - len(shown))
    if omitted and pointer:
        rendered += f"\n…and {omitted} more — full data: {pointer}"
    return rendered


def _byte_cap_with_pointer(text: str, max_bytes: int, pointer: str | None) -> str:
    if max_bytes <= 0 or len(text.encode("utf-8")) <= max_bytes:
        return "" if max_bytes <= 0 else text
    if not pointer:
        return _utf8_prefix(text, max_bytes)
    marker = f"…and more — full data: {pointer}"
    budget = max_bytes - len(marker.encode("utf-8")) - 1
    if budget <= 0:
        return _utf8_prefix(text, max_bytes)
    return _utf8_prefix(text, budget).rstrip() + "\n" + marker


def _utf8_prefix(text: str, max_bytes: int) -> str:
    """Return the longest UTF-8-safe prefix fitting ``max_bytes``."""
    return text.encode("utf-8")[:max_bytes].decode("utf-8", "ignore")


def _context_pointer(context_dir: Any, filename: str, key: str) -> str | None:
    return f".reviewforge-context/{filename} (key: {key})" if context_dir else None


def _trim_review_state(review_context: Any, context_dir: Any) -> Any:
    if not isinstance(review_context, dict):
        return review_context
    inline = dict(review_context)
    for key in ("previousComments", "activeComments", "resolvedComments", "changedCommits", "changedFiles"):
        values = inline.get(key)
        if isinstance(values, list) and len(values) > _CONTEXT_MAX_REVIEW_ITEMS:
            inline[key] = values[:_CONTEXT_MAX_REVIEW_ITEMS]
    return inline


def _review_state_pointers(review_context: Any, context_dir: Any) -> list[str]:
    if not isinstance(review_context, dict) or not context_dir:
        return []
    return [
        f"…and {len(review_context[key]) - _CONTEXT_MAX_REVIEW_ITEMS} more — full data: .reviewforge-context/review-state.json (key: {key})"
        for key in ("previousComments", "activeComments", "resolvedComments", "changedCommits", "changedFiles")
        if isinstance(review_context.get(key), list) and len(review_context[key]) > _CONTEXT_MAX_REVIEW_ITEMS
    ]


def _feedback_section(feedback: Any, context_dir: Any) -> list[str]:
    if not feedback:
        return []
    section = (
        render_section("\nPrevious review feedback:", feedback, _CONTEXT_MAX_ITEMS, _context_pointer(context_dir, "review-state.json", "previousFeedback"))
        if len(feedback) > _CONTEXT_MAX_ITEMS
        else "\nPrevious review feedback:\n" + json.dumps(feedback, ensure_ascii=False, sort_keys=True)
    )
    return [section, "\nDo not re-raise dismissed findings unless the implicated code changed in THIS diff. Treat fixed findings as addressed, but flag them when reintroduced and set regression=true."]


def _prefix_review_state(ctx: StageContext, context_dir: Any) -> list[str]:
    review_context = ctx.extras.get("review_context")
    if not review_context:
        return []
    inline = _trim_review_state(review_context, context_dir)
    pointers = _review_state_pointers(review_context, context_dir)
    parts = ["\nDeterministic review state:\n" + json.dumps(inline, ensure_ascii=False, sort_keys=True) + ("\n" + "\n".join(pointers) if pointers else "")]
    feedback = review_context.get("previousFeedback", []) if isinstance(review_context, dict) else []
    parts.extend(_feedback_section(feedback, context_dir))
    return parts


def _prefix_graph_context(ctx: StageContext, context_dir: Any) -> list[str]:
    parts = []
    if crg := ctx.extras.get("crg_analysis"):
        section = build_crg_section(crg, getattr(ctx.cfg, "crg_context_max_bytes", 8192), context_dir, render_section, _byte_cap_with_pointer)
        if section:
            parts.append("\nDeterministic graph context (Tree-sitter code-review graph):\n" + section)
    if graph := ctx.extras.get("graph_context") and any(getattr(ctx.cfg, name, False) for name in ("graph_api_diff", "graph_flows", "graph_arch")):
        section = build_wave2_section(ctx.extras["graph_context"], getattr(ctx.cfg, "graph_context_max_bytes", 12288), context_dir, render_section, _byte_cap_with_pointer)
        if section:
            parts.append("\n" + section)
    return parts

def _staging_section(index: Any, context_dir: Any) -> str:
    if not isinstance(index, dict) or not context_dir:
        return ""
    names = "\n".join(
        f"  - {name}: {info.get('description', '')}"
        for name, info in sorted(index.items())
        if isinstance(info, dict)
    )
    return "\nDeterministic context files:\n" + names + "\nInline sections are authoritative summaries; read the referenced files for complete data."


def _changed_files_section(ctx: StageContext, context_dir: Any) -> str:
    changed = list(getattr(ctx.state, "files", []) if ctx.state is not None else [])
    if not changed and getattr(ctx, "files_text", ""):
        changed = [line for line in ctx.files_text.splitlines() if line]
    if changed and len(changed) > _CONTEXT_MAX_FILES:
        return render_section("\nChanged files:", changed, _CONTEXT_MAX_FILES, _context_pointer(context_dir, "changed-files.json", "all entries"))
    return "\nChanged files:\n" + (getattr(ctx, "files_text", "") or "\n".join(changed) or "(no changed files)")


def _context_item_sections(ctx: StageContext, context_dir: Any) -> list[str]:
    return [
        render_section(f"\n{label}:", value if isinstance(value, list) else [value], _CONTEXT_MAX_ITEMS, _context_pointer(context_dir, filename, "all entries"))
        for label, value, filename in (
            ("Linked work items", ctx.extras.get("wi_context", []), "work-items.json"),
            ("Existing PR comments", ctx.extras.get("thread_context", []), "threads.json"),
        )
        if value
    ]

def _all_commit_lines(ctx: StageContext) -> list[str]:
    if ctx.artifacts.commits.exists():
        text = ctx.artifacts.commits.read_text(encoding="utf-8")
    elif ctx.state is not None and getattr(ctx.state, "repo_dir", None):
        text = git_ops.run_git(ctx.state.repo_dir, "log", "--oneline", ctx.state.range_spec)
    else:
        text = ""
    return text.splitlines()


def _build_single_pi_prefix(ctx: StageContext, intro: tuple[str, str] | None = None) -> str:
    """Build the shared non-diff prefix for reasoning-engine prompts.

    ``intro`` replaces the two opening lines; the default reproduces the
    historical single-pi wording byte-for-byte.
    """
    metadata = ctx.metadata or (read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {})
    context_dir = ctx.extras.get("context_staging_dir")
    intro_lines = intro or (
        f"Single-call reasoning review for Azure DevOps PR #{ctx.cfg.pr_id}.",
        "Return only the rich ReviewResult JSON object defined in the system prompt.",
    )
    parts = list(intro_lines)
    if section := _staging_section(ctx.extras.get("context_staging_index"), context_dir):
        parts.append(section)
    if metadata:
        parts += ["\nRepository/project metadata:", json.dumps(metadata, ensure_ascii=False)]
    parts.append(_changed_files_section(ctx, context_dir))
    if commits := _all_commit_lines(ctx):
        parts.append(render_section("\nCommits in this PR:", commits, getattr(ctx.cfg, "commit_context_max", 50), _context_pointer(context_dir, "commits.txt", "commits")))
    parts.extend(_context_item_sections(ctx, context_dir))
    parts.extend(_prefix_review_state(ctx, context_dir))
    parts.extend(_prefix_graph_context(ctx, context_dir))
    return "\n".join(parts)
