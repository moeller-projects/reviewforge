"""Stage: produce candidate findings from the diff.

For large diffs, the stage can chunk the work into file-based pieces and
deduplicate findings by (file, line, severity, title, message). The output
goes to ``candidate-findings.json``.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
import json
from dataclasses import replace
from typing import Any

from ...ai.prompts import review_instruction
from ...artifacts.builder import read_json, write_json
from ...git.chunker import build_chunks, build_scopes, scope_document
from ...runlog import info as _log
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext




def _normalize_finding(f: dict[str, Any]) -> dict[str, Any]:
    file = f.get("file")
    if isinstance(file, str) and file.startswith("/"):
        f["file"] = file.lstrip("/")
    return f


def _merge_finding(
    finding: dict[str, Any],
    findings: list[dict[str, Any]],
    seen: dict[tuple, dict[str, Any]],
) -> None:
    key = tuple(
        finding.get(name) or (0 if name == "line" else "")
        for name in ("file", "line", "severity", "title", "message")
    )
    kept = seen.get(key)
    if kept is None:
        kept = _normalize_finding(finding)
        seen[key] = kept
        findings.append(kept)
    else:
        # Duplicate from another chunk: adopt non-keyed extras the kept
        # finding is missing (e.g. evidence, regression).
        for name, value in finding.items():
            if not kept.get(name) and value:
                kept[name] = value

def _fork_runner(ctx: StageContext, worker_id: int) -> Any:
    if type(ctx.pi).__name__ in {"PiCliRunner", "PiRunner"}:
        session_id = f"{ctx.pi.session_id}-chunk-{worker_id}"
        return type(ctx.pi)(ctx.pi.cfg.with_overrides(pi_session_id=session_id))
    return ctx.pi


def _review_one(
    ctx: StageContext,
    cfg: Any,
    diff: str,
    files_text: str,
    out_path: Any,
    label: str = "",
    truncated: bool = False,
    pi_runner: Any = None,
) -> tuple[dict[str, Any], dict[str, int]]:
    text = review_instruction(
        cfg, files_text, ctx.state,
        ctx.extras.get("wi_context", []),
        ctx.extras.get("wi_comments_context", []),
        ctx.extras.get("thread_context", []),
        ctx.artifacts.intent, ctx.artifacts.digest, label, truncated,
    )
    review_context = ctx.extras.get("review_context")
    if review_context:
        text += "\nDETERMINISTIC REVIEW STATE:\n" + json.dumps(review_context, ensure_ascii=False, sort_keys=True) + "\n"
        feedback = review_context.get("previousFeedback", [])
        if feedback:
            text += "\nPREVIOUS REVIEW FEEDBACK:\n" + json.dumps(feedback, ensure_ascii=False, sort_keys=True) + "\nDo not re-raise dismissed findings unless the implicated code changed in THIS diff. Treat fixed findings as addressed, but flag them when reintroduced and set regression=true.\n"
    runner = pi_runner or ctx.pi
    runner.run_json(cfg.review_prompt_path, text + diff, out_path, "reviewer")
    usage = getattr(runner, "token_usage", None)
    usage = usage if isinstance(usage, dict) else getattr(runner, "last_tokens", {})
    usage = usage if isinstance(usage, dict) else {}
    if runner is ctx.pi:
        ctx.last_token_usage = usage
    return read_json(out_path) or {}, {key: int(usage.get(key, 0) or 0) for key in ("in", "out", "total")}


def _merge_chunk_docs(
    ordered_docs: list[tuple[dict[str, Any], dict[str, int]]],
) -> tuple[list[dict[str, Any]], list[str], dict[str, int]]:
    findings: list[dict[str, Any]] = []
    summaries: list[str] = []
    tokens = {"in": 0, "out": 0, "total": 0}
    seen: dict[tuple, dict[str, Any]] = {}
    for doc, usage in ordered_docs:
        for key in tokens:
            tokens[key] += usage[key]
        summaries.append(doc.get("summary", ""))
        for finding in doc.get("findings", []):
            _merge_finding(finding, findings, seen)
    return findings, summaries, tokens


def _run_chunks(ctx: StageContext, cfg: Any, chunks: list[Any], run_one: Any) -> dict[str, Any]:
    import os
    max_workers = max(
        1,
        min(
            len(chunks),
            int(getattr(cfg, "scope_workers", 8) or 1),
            max(2, (os.cpu_count() or 2) // 2),
        ),
    )
    with ThreadPoolExecutor(max_workers=max_workers) as pool:
        futures = {}
        for i, chunk in enumerate(chunks, 1):
            out = ctx.artifacts.dir / "raw" / f"chunk-{i}.json"
            out.parent.mkdir(parents=True, exist_ok=True)
            futures[
                pool.submit(
                    run_one,
                    chunk.diff_text,
                    chunk.files_text,
                    out,
                    f"chunk {i}/{len(chunks)}"
                    + (f" ({chunk.scope_id})" if getattr(chunk, "scope_id", "") else ""),
                    chunk.truncated,
                    _fork_runner(ctx, i),
                )
            ] = i
        ordered = [None] * len(chunks)
        for future in as_completed(futures):
            ordered[futures[future] - 1] = future.result()
    findings, summaries, tokens = _merge_chunk_docs(ordered)
    ctx.extras["_worker_token_usage"] = tokens
    summary = " ".join(item for item in summaries if item).strip()
    if not summary:
        summary = f"Reviewed {len(chunks)} diff chunks."
    elif len(chunks) > 1:
        summary = f"{summary} (across {len(chunks)} diff chunks)"
    payload = {"summary": summary, "findings": findings, "chunks": len(chunks)}
    write_json(ctx.artifacts.candidate, payload)
    return payload
def _review_cache_key(ctx: StageContext, cfg: Any) -> str:
    metadata = {}
    if ctx.artifacts.metadata.exists():
        try:
            metadata = read_json(ctx.artifacts.metadata) or {}
        except Exception:
            metadata = {}
    return cache_key([
        "review_diff", cfg.review_prompt_path.as_posix(),
        metadata.get("sourceCommit"), metadata.get("targetCommit"),
        metadata.get("lastMergeSourceCommit"), ctx.state.diff_text, ctx.files_text,
        ctx.artifacts.intent, ctx.artifacts.digest,
        ctx.extras.get("wi_context", []), ctx.extras.get("wi_comments_context", []),
        ctx.extras.get("thread_context", []), ctx.extras.get("review_context", {}),
        ctx.extras.get("crg_analysis", {}), ctx.extras.get("graph_context", {}),
        cfg.disable_chunk_review, cfg.chunk_trigger_diff_bytes, cfg.max_diff_bytes,
        getattr(cfg, "scope_workers", 8),
    ])


def _single_review(
    ctx: StageContext, cfg: Any, run_one: Any, diff_bytes: int, cache: str
) -> dict[str, Any]:
    if cfg.disable_chunk_review and diff_bytes > cfg.chunk_trigger_diff_bytes:
        _log("DISABLE_CHUNK_REVIEW enabled; reviewing large diff in single pass")
    doc, _usage = run_one(ctx.state.diff_text, ctx.files_text, ctx.artifacts.candidate)
    payload = {
        "summary": doc.get("summary", ""),
        "findings": [_normalize_finding(f) for f in doc.get("findings", [])],
        "chunks": 0,
    }
    write_json(ctx.artifacts.candidate, payload)
    store_cached_json(cfg, "review_diff", cache, payload)
    ctx.candidate = payload
    return {"findings": len(payload.get("findings", [])), "chunks": 0}


class ReviewDiffStage(Stage):
    name = "review_diff"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(ctx.state) and bool(ctx.state.diff_text)

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.state is None:
            return {"findings": 0, "chunks": 0, "truncated": False}
        ctx.artifacts.system_prompt.write_text(ctx.extras.get("system_prompt", ""), encoding="utf-8")
        run_one = lambda diff, files, out, label="", truncated=False, pi_runner=None: _review_one(
            ctx, cfg, diff, files, out, label, truncated, pi_runner
        )
        diff_bytes = len(ctx.state.diff_text.encode())
        review_cache_key = _review_cache_key(ctx, cfg)
        cached = load_cached_json(cfg, "review_diff", review_cache_key)
        if cached:
            _log("review diff cache hit")
            write_json(ctx.artifacts.candidate, cached)
            ctx.candidate = cached
            return {"findings": len(cached.get("findings", [])), "chunks": cached.get("chunks", 0), "cached": True}
        if cfg.disable_chunk_review or diff_bytes <= cfg.chunk_trigger_diff_bytes:
            return _single_review(ctx, cfg, run_one, diff_bytes, review_cache_key)
        _log("diff exceeds chunk trigger; planning review scopes")
        crg = ctx.extras.get("crg_analysis")
        graph_context = ctx.extras.get("graph_context")
        if isinstance(crg, dict) and crg.get("status") in {"ok", "degraded"}:
            chunks, _truncated = build_scopes(
                ctx.state.diff_text,
                list(getattr(ctx.state, "files", [])),
                cfg.max_diff_bytes,
                crg_analysis=crg,
                graph_context=graph_context if isinstance(graph_context, dict) else None,
            )
        else:
            chunks, _truncated = build_chunks(ctx.state, cfg.max_diff_bytes)
        write_json(
            ctx.artifacts.review_scopes,
            scope_document(
                chunks,
                crg_used=isinstance(crg, dict) and crg.get("status") in {"ok", "degraded"},
                max_bytes=cfg.max_diff_bytes,
            ),
        )
        _run_chunks(ctx, cfg, chunks, run_one)
        doc = read_json(ctx.artifacts.candidate) or {"summary": "", "findings": []}
        doc["findings"] = [_normalize_finding(f) for f in doc.get("findings", [])]
        write_json(ctx.artifacts.candidate, doc)
        store_cached_json(cfg, "review_diff", review_cache_key, doc)
        ctx.candidate = doc
        return {
            "findings": len(doc.get("findings", [])),
            "chunks": len(doc.get("findings", [])),
        }


__all__ = ["ReviewDiffStage"]
