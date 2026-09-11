"""Single Pi reasoning engine.

Performs the entire review in one logical reasoning invocation. Python
reduces oversized diff context before calling Pi; optional JSON formatting
repair is tracked by ``PiRunner``. The model may read nearby files through
read-only tools and returns a structured ``ReviewResult``. Compatibility
artifacts are synthesized from the result.
"""
from concurrent.futures import ThreadPoolExecutor, as_completed
import json
import time
from typing import Any, Callable

from pydantic import ValidationError

from ..artifacts.builder import read_json, write_json
from ..exceptions import ReasoningEngineError, SchemaValidationError
from ..git.chunker import DiffChunk, build_scopes, scope_document
from ..git import ops as git_ops
from ..ado.posting import _normalize_title
from ..pipeline.schemas import (
    ChunkResult,
    ChunkSynthesis,
    EscalationHint,
    ReviewConfidence,
    ReviewResult,
    TokenUsage,
)
from ..pipeline.stage import StageContext
from ..runlog import warning as log_warning
from .engine import ReasoningEngine, register_engine
from ..pipeline.crg.prompt import build_crg_section, build_wave2_section

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





def _runner_usage(runner: Any) -> dict[str, int]:
    usage = getattr(runner, "token_usage", None)
    if isinstance(usage, dict):
        return {k: int(usage.get(k, 0) or 0) for k in ("in", "out", "total")}
    usage = getattr(runner, "last_tokens", {})
    if isinstance(usage, dict):
        return {k: int(usage.get(k, 0) or 0) for k in ("in", "out", "total")}
    return {}


def _runner_count(runner: Any, name: str) -> int:
    value = getattr(runner, name, 0)
    return value if isinstance(value, int) else 0


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

def _build_single_pi_prefix(ctx: StageContext) -> str:
    """Build the shared non-diff prefix for single-pi prompts."""
    metadata = ctx.metadata or (read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {})
    context_dir = ctx.extras.get("context_staging_dir")
    parts = [
        f"Single-call reasoning review for Azure DevOps PR #{ctx.cfg.pr_id}.",
        "Return only the rich ReviewResult JSON object defined in the system prompt.",
    ]
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


def _utf8_prefix(text: str, max_bytes: int) -> str:
    """Return the longest UTF-8-safe prefix fitting ``max_bytes``."""
    return text.encode("utf-8")[:max_bytes].decode("utf-8", "ignore")

def _all_commit_lines(ctx: StageContext) -> list[str]:
    if ctx.artifacts.commits.exists():
        text = ctx.artifacts.commits.read_text(encoding="utf-8")
    elif ctx.state is not None and getattr(ctx.state, "repo_dir", None):
        text = git_ops.run_git(ctx.state.repo_dir, "log", "--oneline", ctx.state.range_spec)
    else:
        text = ""
    return text.splitlines()


def _diff_chunks(diff_text: str, max_bytes: int) -> list[str]:
    """Partition a unified diff at file boundaries in stable source order."""
    if len(diff_text.encode("utf-8")) <= max_bytes:
        return [diff_text]
    sections = [f"diff --git {part}" for part in diff_text.split("diff --git ") if part]
    chunks: list[str] = []
    current = ""
    for section in sections:
        if current and len((current + section).encode("utf-8")) > max_bytes:
            chunks.append(current)
            current = ""
        current += section
    if current:
        chunks.append(current)
    return chunks


def _build_single_pi_instruction(ctx: StageContext) -> str:
    """Build the user message for a non-chunked reasoning review."""
    prefix = _build_single_pi_prefix(ctx)
    diff_text = getattr(ctx.state, "diff_text", "") or (
        ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
    )
    parts = [prefix]
    if diff_text:
        parts += ["\nUnified diff:\n", diff_text]
    return "\n".join(parts) + "\nReturn only the ReviewResult JSON object defined in the system prompt.\n"

def _build_chunk_instruction(
    ctx: StageContext,
    scope: DiffChunk | str,
    index: int,
    total: int,
    *,
    include_shared_prefix: bool,
) -> str:
    if isinstance(scope, str):
        scope = DiffChunk(diff_text=scope, files_text="", scope_id=f"scope-{index:02d}")
    prefix = _build_single_pi_prefix(ctx) if include_shared_prefix or index == 1 else ""
    scope_meta = [
        f"Review scope {scope.scope_id or f'scope-{index:02d}'}/{total}.",
        f"Primary files: {scope.files_text.strip() or '(none)'}",
        f"Context-only files: {', '.join(scope.context_files) or '(none)'}",
        f"Affected flows: {', '.join(scope.affected_flows) or '(none)'}",
        f"Bridge files: {', '.join(scope.bridge_files) or '(none)'}",
        "Report findings for primary files. Use context-only and bridge files "
        "to understand behavior; do not duplicate findings for them.",
        "Unified diff scope:",
        scope.diff_text,
    ]
    body = (
        "\n".join(scope_meta)
        + "\nReturn only a JSON object with findings, test_gaps, uncertainties, "
        "escalation_hints, and discarded_findings; do not summarize the PR.\n"
    )
    return f"{prefix}\n\n{body}" if prefix else body


def _format_finding_line(finding: dict[str, Any]) -> str:
    location = finding.get("file") or "general"
    if finding.get("line"):
        location = f"{location}:{finding['line']}"
    return f"- [{finding.get('severity', 'minor')}] {finding.get('title', '')} ({location})"


def _synthesis_section(
    title: str,
    items: list[dict[str, Any]],
    formatter: Callable[[dict[str, Any]], str],
) -> list[str]:
    if not items:
        return [title, "- none"]
    return [title, *(formatter(item) for item in items)]


def _finding_synthesis_lines(findings: list[dict[str, Any]]) -> list[str]:
    lines = [_format_finding_line(finding) for finding in findings]
    lines.append("- none" if not findings else "")
    return lines


def _format_gap(gap: dict[str, Any]) -> str:
    return f"- {gap.get('file', '')}: {gap.get('behavior', '')}"


def _format_hint(hint: dict[str, Any]) -> str:
    return f"- [{hint.get('danger', 'high')}] {hint.get('suggested_focus', '')}: {hint.get('reason', '')}"


def _format_discarded(item: dict[str, Any]) -> str:
    return f"- {item.get('category', '')}: {item.get('reason', '')}"


def _format_uncertainty(item: dict[str, Any]) -> str:
    return f"- {item.get('topic', '')}"


def _synthesis_lines(
    findings: list[dict[str, Any]],
    uncertainties: list[dict[str, Any]],
    test_gaps: list[dict[str, Any]],
    escalation_hints: list[dict[str, Any]],
    discarded_findings: list[dict[str, Any]],
) -> list[str]:
    lines = _finding_synthesis_lines(findings)
    lines.extend(_synthesis_section("Merged test gaps across all chunks:", test_gaps, _format_gap))
    lines.extend(
        _synthesis_section(
            "Merged escalation hints across all chunks:", escalation_hints, _format_hint
        )
    )
    lines.extend(
        _synthesis_section(
            "Merged discarded findings across all chunks:", discarded_findings, _format_discarded
        )
    )
    lines.extend(
        _synthesis_section("Merged uncertainties across all chunks:", uncertainties, _format_uncertainty)
    )
    return lines



def _build_synthesis_instruction(
    chunk_count: int,
    findings: list[dict[str, Any]],
    uncertainties: list[dict[str, Any]],
    test_gaps: list[dict[str, Any]] | None = None,
    escalation_hints: list[dict[str, Any]] | None = None,
    discarded_findings: list[dict[str, Any]] | None = None,
) -> str:
    """Build the whole-PR synthesis request; no diff is re-sent."""
    lines = [
        f"You reviewed this pull request in {chunk_count} coherent diff chunks.",
        "Base the synthesis on the merged scope results below. Scope workers "
        "may use isolated sessions, so this merged data is authoritative.",
        "",
        "Merged findings across all scopes:",
    ]
    lines.extend(
        _synthesis_lines(
            findings,
            uncertainties or [],
            test_gaps or [],
            escalation_hints or [],
            discarded_findings or [],
        )
    )
    lines.extend(["", "Return only the chunk-synthesis JSON object defined in the system prompt."])
    return "\n".join(lines)


def _select_chunks(ctx: StageContext, diff_text: str) -> list[DiffChunk]:
    cfg = ctx.cfg
    files = list(getattr(ctx.state, "files", []) if ctx.state is not None else [])
    if cfg.disable_chunk_review:
        log_warning("DISABLE_CHUNK_REVIEW enabled; forcing single-pass reasoning")
        return [DiffChunk(diff_text, "\n".join(files) + ("\n" if files else ""), scope_id="scope-01")]
    if len(diff_text.encode("utf-8")) <= cfg.chunk_trigger_diff_bytes:
        return [DiffChunk(diff_text, "\n".join(files) + ("\n" if files else ""), scope_id="scope-01")]
    log_warning(
        f"diff exceeds CHUNK_TRIGGER_DIFF_BYTES ({cfg.chunk_trigger_diff_bytes}); "
        "using CRG-aware chunked single-pi reasoning"
    )
    scopes, _ = build_scopes(
        diff_text,
        files,
        cfg.max_diff_bytes,
        crg_analysis=ctx.extras.get("crg_analysis"),
        graph_context=ctx.extras.get("graph_context"),
    )
    return scopes


def _format_validation_error(exc: Exception) -> str:
    """Render a Pydantic ``ValidationError`` as a compact field-level message."""
    if not isinstance(exc, ValidationError):
        return str(exc)
    parts = []
    for item in exc.errors(include_url=False):
        loc = ".".join(str(part) for part in item.get("loc", ()))
        msg = item.get("msg", "")
        parts.append(f"{loc}: {msg}" if loc else msg)
    return "; ".join(parts) or str(exc)


def _single_pass(ctx: StageContext, cfg: Any) -> ReviewResult:
    output_path = ctx.artifacts.raw_dir / "fast-review.json"
    ctx.pi.run_json(cfg.fast_review_prompt_path, _build_single_pi_instruction(ctx), output_path, "single-pi reasoning")
    raw = read_json(output_path)
    if raw is None:
        raise ReasoningEngineError("single-pi reasoning produced no JSON", details={"output_path": str(output_path)})
    try:
        return ReviewResult.model_validate(raw)
    except Exception as exc:
        raise SchemaValidationError(
            f"single-pi response does not match ReviewResult schema: {_format_validation_error(exc)}",
            details={"error": str(exc), "output_path": str(output_path)},
        ) from exc


def _new_merge_state() -> dict[str, Any]:
    return {
        "findings": [],
        "test_gaps": [],
        "uncertainties": [],
        "escalation_hints": [],
        "discarded_findings": [],
        "seen_findings": set(),
        "seen_gaps": set(),
        "seen_hints": set(),
        "seen_uncertainties": set(),
        "seen_discarded": set(),
    }


def _merge_unique(
    state: dict[str, Any],
    items: list[Any],
    seen_key: str,
    output_key: str,
    key_func: Callable[[Any], Any],
) -> None:
    for item in items:
        key = key_func(item)
        if key in state[seen_key]:
            continue
        state[seen_key].add(key)
        state[output_key].append(item.model_dump(by_alias=True))


def _finding_key(finding: Any) -> tuple[Any, ...]:
    return (finding.file, finding.line, _normalize_title(finding.title))


def _gap_key(gap: Any) -> tuple[str, str]:
    return (gap.file, gap.behavior.casefold().strip())


def _hint_key(hint: Any) -> tuple[tuple[str, ...], str]:
    return (tuple(sorted(hint.files)), hint.reason.casefold().strip())


def _uncertainty_key(item: Any) -> tuple[str, str]:
    return (item.topic.casefold().strip(), item.reason.casefold().strip())


def _discarded_key(item: Any) -> tuple[str, str]:
    return (item.category.casefold().strip(), item.reason.casefold().strip())


def _merge_chunk(partial: ChunkResult, state: dict[str, Any]) -> None:
    _merge_unique(state, partial.findings, "seen_findings", "findings", _finding_key)
    _merge_unique(state, partial.test_gaps, "seen_gaps", "test_gaps", _gap_key)
    _merge_unique(state, partial.escalation_hints, "seen_hints", "escalation_hints", _hint_key)
    _merge_unique(state, partial.uncertainties, "seen_uncertainties", "uncertainties", _uncertainty_key)
    _merge_unique(
        state, partial.discarded_findings, "seen_discarded", "discarded_findings",
        _discarded_key,
    )



def _cap_and_order_hints(hints: list[dict[str, Any]]) -> list[dict[str, Any]]:
    ordered = sorted(hints, key=lambda h: 0 if h.get("danger") == "critical" else 1)
    return ordered[:3]


def _fork_scope_runner(ctx: StageContext, worker_id: int) -> Any:
    if type(ctx.pi).__name__ in {"PiCliRunner", "PiRunner"}:
        session_id = f"{ctx.pi.session_id}-scope-{worker_id}"
        return type(ctx.pi)(ctx.pi.cfg.with_overrides(pi_session_id=session_id))
    return ctx.pi


def _review_scope(
    ctx: StageContext,
    cfg: Any,
    scope: DiffChunk,
    index: int,
    total: int,
) -> tuple[ChunkResult, TokenUsage]:
    output_path = ctx.artifacts.raw_dir / f"fast-review-{index}.json"
    runner = _fork_scope_runner(ctx, index)
    before = _runner_usage(runner)
    runner.run_json(
        cfg.fast_review_prompt_path,
        _build_chunk_instruction(ctx, scope, index, total, include_shared_prefix=True),
        output_path,
        f"single-pi chunk/scope {index}/{total}",
    )
    try:
        partial = ChunkResult.model_validate(read_json(output_path))
    except Exception as exc:
        raise SchemaValidationError(
            f"single-pi scope response does not match ChunkResult schema: "
            f"{_format_validation_error(exc)}",
            details={"scope_id": scope.scope_id, "output_path": str(output_path)},
        ) from exc
    after = _runner_usage(runner)
    return partial, TokenUsage(
        input=max(0, after.get("in", 0) - before.get("in", 0)),
        output=max(0, after.get("out", 0) - before.get("out", 0)),
        total=max(0, after.get("total", 0) - before.get("total", 0)),
    )


def _run_scope_workers(
    ctx: StageContext, cfg: Any, scopes: list[DiffChunk]
) -> list[tuple[ChunkResult, TokenUsage] | None]:
    ordered: list[tuple[ChunkResult, TokenUsage] | None] = [None] * len(scopes)
    workers = max(1, min(len(scopes), int(getattr(cfg, "scope_workers", 8) or 1)))
    if type(ctx.pi).__name__ not in {"PiCliRunner", "PiRunner"}:
        workers = 1
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = {
            pool.submit(_review_scope, ctx, cfg, scope, index, len(scopes)): index - 1
            for index, scope in enumerate(scopes, 1)
        }
        for future in as_completed(futures):
            ordered[futures[future]] = future.result()
    return ordered


def _merge_scope_results(
    ordered: list[tuple[ChunkResult, TokenUsage] | None],
) -> tuple[dict[str, Any], list[TokenUsage]]:
    state = _new_merge_state()
    usage: list[TokenUsage] = []
    for item in ordered:
        if item is None:
            raise ReasoningEngineError("scope review completed without a result")
        partial, tokens = item
        usage.append(tokens)
        _merge_chunk(partial, state)
    return state, usage


def _chunked_pass(
    engine: Any, ctx: StageContext, cfg: Any, scopes: list[DiffChunk]
) -> tuple[ReviewResult, list[TokenUsage]]:
    state, usage = _merge_scope_results(_run_scope_workers(ctx, cfg, scopes))
    findings = state["findings"]
    test_gaps = state["test_gaps"][:5]
    escalation_hints = _cap_and_order_hints(state["escalation_hints"])
    uncertainties = state["uncertainties"]
    discarded_findings = state["discarded_findings"]
    synthesis = engine._synthesize(
        ctx, len(scopes), findings, uncertainties, test_gaps, escalation_hints, discarded_findings
    )
    payload = (
        {
            "review_summary": synthesis.review_summary.model_dump(),
            "verification_summary": synthesis.verification_summary.model_dump(),
            "pr_summary": synthesis.pr_summary.model_dump(),
            "good_practices": [gp.model_dump() for gp in synthesis.good_practices],
        }
        if synthesis is not None
        else {
            "review_summary": {"summary": f"Reviewed {len(scopes)} coherent diff chunks."},
            "verification_summary": {"summary": "Reviewed each deterministic diff scope.", "approach": "parallel scoped diff review"},
            "pr_summary": {
                "intent": f"Pull request reviewed across {len(scopes)} coherent scopes.",
                "work_type": "change",
                "biggest_unknown": "scope synthesis unavailable",
                "implementation_summary": f"Reviewed {len(scopes)} coherent diff chunks.",
            },
        }
    )
    if synthesis is None:
        ctx.extras["_synthesis_fallback"] = True
    payload.update(
        findings=findings,
        test_gaps=test_gaps,
        uncertainties=uncertainties,
        escalation_hints=escalation_hints,
        discarded_findings=discarded_findings,
    )
    return ReviewResult.model_validate(payload), usage


def _graph_architecture_present(ctx: StageContext) -> bool:
    graph = ctx.extras.get("graph_context")
    if not isinstance(graph, dict):
        return False
    architecture = graph.get("architecture")
    return isinstance(architecture, dict) and architecture.get("status") == "ok"


def _confidence_rank(level: str | None) -> int:
    return {"low": 1, "medium": 2, "high": 3}.get(level or "", 1)


def _rank_to_confidence(rank: int) -> str:
    return {1: "low", 2: "medium", 3: "high"}[max(1, min(3, rank))]


def _derive_review_confidence(result: ReviewResult) -> tuple[str, list[str]]:
    base = min(
        (_confidence_rank(f.confidence) for f in result.findings),
        default=3,
    )
    downgrade_reasons: list[str] = []
    if result.pr_summary.biggest_unknown:
        downgrade_reasons.append("biggest_unknown unresolved")
    if any(u.topic.startswith("cross-chunk:") for u in result.uncertainties):
        downgrade_reasons.append("unresolved cross-chunk uncertainty")
    rank = max(1, base - (1 if downgrade_reasons else 0))
    level = _rank_to_confidence(rank)
    reasons: list[str] = []
    if not result.findings:
        reasons.append("no findings; context was sufficient")
    else:
        reasons.append("derived from lowest reported finding confidence")
    reasons.extend(downgrade_reasons)
    return level, reasons


def _evidence_counts(result: ReviewResult) -> dict[str, int]:
    tests: set[str] = set()
    symbols: set[str] = set()
    work_items: set[str] = set()
    for finding in result.findings:
        evidence = finding.evidence
        tests.update(evidence.testsRead)
        work_items.update(evidence.workItems)
        symbols.update(symbol.name for symbol in evidence.symbols if symbol.name)
    return {
        "testsRead": len(tests),
        "symbolsInspected": len(symbols),
        "workItemsRead": len(work_items),
    }


def _normalize_review(result: ReviewResult, ctx: StageContext) -> ReviewResult:
    """Apply deterministic post-processing the model cannot know."""
    if not _graph_architecture_present(ctx):
        result.pr_summary.architectural_impact = "no significant architectural impact"
    level, reasons = _derive_review_confidence(result)
    result.review_confidence = ReviewConfidence(level=level, reasons=reasons)
    counts = _evidence_counts(result)
    result.metrics = result.metrics.model_copy(
        update={
            "confidence": level,
            "testsRead": max(result.metrics.testsRead, counts["testsRead"]),
            "symbolsInspected": max(result.metrics.symbolsInspected, counts["symbolsInspected"]),
            "workItemsRead": max(result.metrics.workItemsRead, counts["workItemsRead"]),
        }
    )
    return result


def _escalation_instruction(hints: list[EscalationHint], diff_text: str) -> str:
    files = sorted({f for hint in hints for f in hint.files})
    lines = [
        "Focused escalation review for the file set named below. Re-review only "
        "these files for the listed risks and return the full ReviewResult JSON object.",
        "",
        "Files:",
        *[f"- {f}" for f in files],
        "",
        "Escalation hints:",
    ]
    for hint in hints:
        lines.append(f"- [{hint.danger}] {hint.suggested_focus}: {hint.reason} ({', '.join(hint.files)})")
    lines.extend(["", "Unified diff:", diff_text])
    return "\n".join(lines) + "\nReturn only the ReviewResult JSON object defined in the system prompt.\n"


def _escalation_runner(ctx: StageContext, cfg: Any) -> Any:
    model = getattr(cfg, "escalation_model", None)
    if not model or model == cfg.pi_model:
        return ctx.pi
    from ..ai.model_runner import create_model_runner

    session_id = getattr(ctx.pi, "session_id", None)
    overrides: dict[str, Any] = {"pi_model": model}
    if session_id:
        overrides["pi_session_id"] = f"{session_id}-escalation"
    runner = create_model_runner(cfg.with_overrides(**overrides))
    set_working_dir = getattr(runner, "set_working_dir", None)
    if callable(set_working_dir):
        set_working_dir(getattr(ctx.state, "repo_dir", None))
    return runner


def _execute_escalation(
    runner: Any,
    cfg: Any,
    instruction: str,
    output_path: Any,
) -> ReviewResult | None:
    try:
        runner.run_json(cfg.fast_review_prompt_path, instruction, output_path, "escalation review")
        return ReviewResult.model_validate(read_json(output_path))
    except Exception as exc:  # noqa: BLE001 - escalation must never fail a review
        log_warning(
            f"escalation review unavailable ({type(exc).__name__}: {exc}); retaining primary review"
        )
        return None


def _run_escalation_pass(
    engine: Any, ctx: StageContext, result: ReviewResult
) -> ReviewResult | None:
    """Optionally run a focused deeper pass over escalation-hinted files."""
    cfg = ctx.cfg
    if not getattr(cfg, "escalation_review_enabled", False) or not result.escalation_hints:
        return None
    diff_text = getattr(ctx.state, "diff_text", "") or ""
    instruction = _escalation_instruction(list(result.escalation_hints), diff_text)
    output_path = ctx.artifacts.raw_dir / "escalation-review.json"
    runner = _escalation_runner(ctx, cfg)
    focused = _execute_escalation(runner, cfg, instruction, output_path)
    if focused is None:
        return None
    if runner is not ctx.pi:
        ctx.extras["_escalation_usage"] = _runner_usage(runner)
    return focused



def _merge_escalation(base: ReviewResult, focused: ReviewResult) -> ReviewResult:
    """Incorporate a focused pass: it replaces revisited findings, adds new ones."""
    focused_keys = {
        (f.file, f.line, _normalize_title(f.title)) for f in focused.findings
    }
    findings = [f.model_dump(by_alias=True) for f in focused.findings]
    findings.extend(
        f.model_dump(by_alias=True)
        for f in base.findings
        if (f.file, f.line, _normalize_title(f.title)) not in focused_keys
    )
    gaps: dict[tuple[str, str], dict[str, Any]] = {}
    for gap in base.test_gaps:
        gaps[(gap.file, gap.behavior.casefold())] = gap.model_dump(by_alias=True)
    for gap in focused.test_gaps:
        gaps[(gap.file, gap.behavior.casefold())] = gap.model_dump(by_alias=True)
    test_gaps = list(gaps.values())[:5]
    hints: dict[tuple[tuple[str, ...], str], dict[str, Any]] = {}
    for hint in base.escalation_hints:
        hints[(tuple(sorted(hint.files)), hint.reason.casefold())] = hint.model_dump(by_alias=True)
    for hint in focused.escalation_hints:
        hints[(tuple(sorted(hint.files)), hint.reason.casefold())] = hint.model_dump(by_alias=True)
    escalation_hints = _cap_and_order_hints(list(hints.values()))
    uncertainties = [
        u.model_dump(by_alias=True) for u in (*base.uncertainties, *focused.uncertainties)
    ]
    discarded = [
        d.model_dump(by_alias=True)
        for d in (*base.discarded_findings, *focused.discarded_findings)
    ]
    payload = base.model_dump(by_alias=True)
    payload.update(
        findings=findings,
        test_gaps=test_gaps,
        uncertainties=uncertainties,
        escalation_hints=escalation_hints,
        discarded_findings=discarded,
    )
    return ReviewResult.model_validate(payload)


def _update_metrics(
    result: ReviewResult, ctx: StageContext, started_at: float,
    finished_at: float, reasoning_duration_ms: int, chunks: list[DiffChunk], chunk_usage: list[TokenUsage],
) -> ReviewResult:
    tokens = _runner_usage(ctx.pi)
    escalation = ctx.extras.get("_escalation_usage") or {}
    tokens = {
        "in": tokens.get("in", 0) + int(escalation.get("in", 0) or 0),
        "out": tokens.get("out", 0) + int(escalation.get("out", 0) or 0),
        "total": tokens.get("total", 0) + int(escalation.get("total", 0) or 0),
    }
    result.metrics = result.metrics.model_copy(update={
        "piInputTokens": tokens.get("in", 0), "piOutputTokens": tokens.get("out", 0),
        "piTotalTokens": tokens.get("total", 0), "invocationCount": _runner_count(ctx.pi, "invocation_count"),
        "repairInvocationCount": _runner_count(ctx.pi, "repair_invocation_count"),
        "wallClockDurationMs": int((finished_at - started_at) * 1000),
        "reasoningDurationMs": reasoning_duration_ms, "projectionDurationMs": 0,
        "validationDurationMs": 0, "changedFilesReviewed": len(getattr(ctx.state, "files", [])),
        "chunkCount": len(chunks), "chunkTokenUsage": chunk_usage,
    })
    return result
class SinglePiReasoningEngine(ReasoningEngine):
    """One Pi call that returns a full ``ReviewResult``."""

    def __init__(self, cfg: Any | None = None) -> None:
        self._cfg = cfg

    @property
    def name(self) -> str:
        return "single_pi"

    def execute(self, ctx: StageContext) -> ReviewResult:
        cfg = ctx.cfg
        diff_text = getattr(ctx.state, "diff_text", "") or (
            ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
        )
        chunks = _select_chunks(ctx, diff_text)
        crg = ctx.extras.get("crg_analysis")
        crg_status = crg.get("status") if isinstance(crg, dict) else None
        write_json(
            ctx.artifacts.review_scopes,
            scope_document(
                chunks,
                crg_used=crg_status in {"ok", "degraded"},
                max_bytes=cfg.max_diff_bytes,
            ),
        )
        started_at = time.time()
        reasoning_started = time.perf_counter()
        if len(chunks) == 1:
            result = _single_pass(ctx, cfg)
            chunk_usage: list[TokenUsage] = []
        else:
            result, chunk_usage = _chunked_pass(self, ctx, cfg, chunks)
        focused = _run_escalation_pass(self, ctx, result)
        if focused is not None:
            result = _merge_escalation(result, focused)
        result = _normalize_review(result, ctx)
        reasoning_duration_ms = int((time.perf_counter() - reasoning_started) * 1000)
        tokens = _runner_usage(ctx.pi)
        ctx.last_token_usage = tokens
        finished_at = time.time()
        result = self._enrich_metadata(result, cfg, started_at, finished_at, tokens)
        return _update_metrics(result, ctx, started_at, finished_at, reasoning_duration_ms, chunks, chunk_usage)

    def _synthesize(
        self,
        ctx: StageContext,
        chunk_count: int,
        findings: list[dict[str, Any]],
        uncertainties: list[dict[str, Any]],
        test_gaps: list[dict[str, Any]] | None = None,
        escalation_hints: list[dict[str, Any]] | None = None,
        discarded_findings: list[dict[str, Any]] | None = None,
    ) -> ChunkSynthesis | None:
        """Ask Pi for whole-PR summaries; ``None`` means use boilerplate."""
        output_path = ctx.artifacts.raw_dir / "chunk-synthesis.json"
        try:
            ctx.pi.run_json(
                ctx.cfg.chunk_synthesis_prompt_path,
                _build_synthesis_instruction(
                    chunk_count, findings, uncertainties,
                    test_gaps, escalation_hints, discarded_findings,
                ),
                output_path,
                "single-pi synthesis",
            )
            return ChunkSynthesis.model_validate(read_json(output_path))
        except Exception as exc:  # noqa: BLE001 - summaries must never fail a review
            log_warning(
                f"chunk synthesis unavailable ({type(exc).__name__}: {exc}); "
                "falling back to deterministic summaries"
            )
            return None

    def _enrich_metadata(
        self,
        result: ReviewResult,
        cfg: Any,
        started_at: float,
        finished_at: float,
        tokens: dict[str, int] | None,
    ) -> ReviewResult:
        """Fill in run metadata that the model cannot know."""
        from ..pipeline.schemas import ModelMetadata, ReviewMetadata, TokenUsage

        tokens = tokens or {}

        result.metadata = ReviewMetadata(
            started_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(started_at)),
            finished_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(finished_at)),
            duration_ms=int((finished_at - started_at) * 1000),
            model=ModelMetadata(
                model=cfg.pi_model,
                reasoning_engine=self.name,
            ),
            tokens=TokenUsage(
                input=int(tokens.get("in", 0) or 0),
                output=int(tokens.get("out", 0) or 0),
                total=int(tokens.get("total", 0) or 0),
            ),
        )
        return result



register_engine(SinglePiReasoningEngine().name, SinglePiReasoningEngine)
