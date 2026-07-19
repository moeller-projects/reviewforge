"""Stage: produce candidate findings from the diff.

For large diffs, the stage can chunk the work into file-based pieces and
deduplicate findings by (file, line, severity, title, message). The output
goes to ``candidate-findings.json``.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import replace
from typing import Any

from ...ai.prompts import review_instruction
from ...artifacts.builder import read_json, write_json
from ...git.chunker import build_chunks
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


def _normalize_finding(f: dict[str, Any]) -> dict[str, Any]:
    file = f.get("file")
    if isinstance(file, str) and file.startswith("/"):
        f["file"] = file.lstrip("/")
    return f


class ReviewDiffStage(Stage):
    name = "review_diff"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(ctx.state) and bool(ctx.state.diff_text)

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if ctx.state is None:
            return {"findings": 0, "chunks": 0, "truncated": False}

        ctx.artifacts.system_prompt.write_text(
            ctx.extras.get("system_prompt", ""), encoding="utf-8"
        )

        def _fork_runner(worker_id: int):
            if type(ctx.pi).__name__ == "PiRunner" and hasattr(ctx.pi, "cfg"):
                # Chunk workers run concurrently; each needs an isolated Pi
                # session to avoid concurrent writes to shared session state.
                session_id = f"{ctx.pi.session_id}-chunk-{worker_id}"
                runner_cfg = ctx.pi.cfg.with_overrides(pi_session_id=session_id)
                return type(ctx.pi)(runner_cfg)
            return ctx.pi

        def run_one(
            diff: str,
            files_text: str,
            out_path,
            label: str = "",
            truncated: bool = False,
            *,
            pi_runner=None,
        ) -> tuple[dict[str, Any], dict[str, int]]:
            text = review_instruction(
                cfg,
                files_text,
                ctx.state,
                ctx.extras.get("wi_context", []),
                ctx.extras.get("wi_comments_context", []),
                ctx.extras.get("thread_context", []),
                ctx.artifacts.intent,
                ctx.artifacts.digest,
                label,
                truncated,
            ) + diff
            runner = pi_runner or ctx.pi
            runner.run_json(cfg.review_prompt_path, text, out_path, "reviewer")
            usage = getattr(runner, "token_usage", None)
            if not isinstance(usage, dict):
                usage = getattr(runner, "last_tokens", {})
            usage = usage if isinstance(usage, dict) else {}
            if runner is ctx.pi:
                ctx.last_token_usage = usage
            return read_json(out_path) or {}, {
                key: int(usage.get(key, 0) or 0) for key in ("in", "out", "total")
            }

        diff_bytes = len(ctx.state.diff_text.encode())
        metadata: dict[str, Any] = {}
        if ctx.artifacts.metadata.exists():
            try:
                metadata = read_json(ctx.artifacts.metadata) or {}
            except Exception:
                # Corrupt metadata should not break the review. The cache
                # key falls back to "" for the missing fields, which is
                # still deterministic per run.
                metadata = {}
        review_cache_key = cache_key([
            "review_diff",
            cfg.review_prompt_path.as_posix(),
            metadata.get("sourceCommit"),
            metadata.get("targetCommit"),
            metadata.get("lastMergeSourceCommit"),
            ctx.state.diff_text,
            ctx.files_text,
            ctx.artifacts.intent,
            ctx.artifacts.digest,
            ctx.extras.get("wi_context", []),
            ctx.extras.get("wi_comments_context", []),
            ctx.extras.get("thread_context", []),
            cfg.disable_chunk_review,
            cfg.chunk_trigger_diff_bytes,
            cfg.max_diff_bytes,
        ])
        cached = load_cached_json(cfg, "review_diff", review_cache_key)
        if cached:
            _log("review diff cache hit")
            write_json(ctx.artifacts.candidate, cached)
            ctx.candidate = cached
            return {"findings": len(cached.get("findings", [])), "chunks": cached.get("chunks", 0), "cached": True}
        if cfg.disable_chunk_review or diff_bytes <= cfg.chunk_trigger_diff_bytes:
            if cfg.disable_chunk_review and diff_bytes > cfg.chunk_trigger_diff_bytes:
                _log("DISABLE_CHUNK_REVIEW enabled; reviewing large diff in single pass")
            doc, _usage = run_one(ctx.state.diff_text, ctx.files_text, ctx.artifacts.candidate)
            payload = {"summary": doc.get("summary", ""), "findings": [_normalize_finding(f) for f in doc.get("findings", [])], "chunks": 0}
            write_json(ctx.artifacts.candidate, payload)
            store_cached_json(cfg, "review_diff", review_cache_key, payload)
            ctx.candidate = payload
            return {"findings": len(payload.get("findings", [])), "chunks": 0}
        else:
            _log("diff exceeds chunk trigger; splitting file-based chunks")
            chunks, truncated_any = build_chunks(ctx.state, cfg.max_diff_bytes)
            findings_list: list[dict[str, Any]] = []
            summaries: list[str] = []
            seen: set[tuple] = set()
            import os
            max_workers = max(1, min(len(chunks), max(2, (os.cpu_count() or 2) // 2), 8))
            with ThreadPoolExecutor(max_workers=max_workers) as pool:
                future_map = {}
                for i, ch in enumerate(chunks, 1):
                    out = ctx.artifacts.dir / "raw" / f"chunk-{i}.json"
                    out.parent.mkdir(parents=True, exist_ok=True)
                    future = pool.submit(run_one, ch.diff_text, ch.files_text, out, f"chunk {i}/{len(chunks)}", ch.truncated, pi_runner=_fork_runner(i))
                    future_map[future] = (i, out)
                ordered_docs: list[tuple[dict[str, Any], dict[str, int]]] = [None] * len(chunks)  # type: ignore[list-item]
                for future in as_completed(future_map):
                    idx, _out = future_map[future]
                    ordered_docs[idx - 1] = future.result()
                worker_tokens = {"in": 0, "out": 0, "total": 0}
                for doc, usage in ordered_docs:
                    for key in worker_tokens:
                        worker_tokens[key] += usage[key]
                    summaries.append(doc.get("summary", ""))
                    for f in doc.get("findings", []):
                        key = (
                            f.get("file") or "",
                            f.get("line") or 0,
                            f.get("severity") or "",
                            f.get("title") or "",
                            f.get("message") or "",
                        )
                        if key in seen:
                            continue
                        seen.add(key)
                        findings_list.append(_normalize_finding(f))
            ctx.extras["_worker_token_usage"] = worker_tokens
            summary = " ".join(s for s in summaries if s).strip()
            if not summary:
                summary = f"Reviewed {len(chunks)} diff chunks."
            elif len(chunks) > 1:
                summary = f"{summary} (across {len(chunks)} diff chunks)"
            write_json(
                ctx.artifacts.candidate,
                {"summary": summary, "findings": findings_list},
            )

        doc = read_json(ctx.artifacts.candidate) or {"summary": "", "findings": []}
        # Normalize file paths once more on the consolidated doc.
        doc["findings"] = [_normalize_finding(f) for f in doc.get("findings", [])]
        write_json(ctx.artifacts.candidate, doc)
        ctx.candidate = doc
        return {
            "findings": len(doc.get("findings", [])),
            "chunks": int(not (cfg.disable_chunk_review or diff_bytes <= cfg.chunk_trigger_diff_bytes)),
        }


__all__ = ["ReviewDiffStage"]
