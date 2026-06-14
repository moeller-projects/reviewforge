"""Stage: produce candidate findings from the diff.

For large diffs, the stage can chunk the work into file-based pieces and
deduplicate findings by (file, line, severity, title, message). The output
goes to ``candidate-findings.json``.
"""
from __future__ import annotations

import shutil
from typing import Any

from ...ai.prompts import review_instruction
from ...artifacts.builder import read_json, write_json
from ...git.chunker import build_chunks
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

        def run_one(
            diff: str,
            files_text: str,
            out_path,
            label: str = "",
            truncated: bool = False,
        ) -> dict[str, Any]:
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
            ctx.pi.run_json(cfg.review_prompt_path, text, out_path, "reviewer")
            return read_json(out_path) or {}

        diff_bytes = len(ctx.state.diff_text.encode())
        if cfg.disable_chunk_review or diff_bytes <= cfg.chunk_trigger_diff_bytes:
            if cfg.disable_chunk_review and diff_bytes > cfg.chunk_trigger_diff_bytes:
                _log("DISABLE_CHUNK_REVIEW enabled; reviewing large diff in single pass")
            run_one(ctx.state.diff_text, ctx.files_text, ctx.artifacts.candidate)
        else:
            _log("diff exceeds chunk trigger; splitting file-based chunks")
            chunks, truncated_any = build_chunks(ctx.state, cfg.max_diff_bytes)
            findings_list: list[dict[str, Any]] = []
            summaries: list[str] = []
            seen: set[tuple] = set()
            for i, ch in enumerate(chunks, 1):
                out = ctx.artifacts.dir / "raw" / f"chunk-{i}.json"
                out.parent.mkdir(parents=True, exist_ok=True)
                doc = run_one(ch.diff_text, ch.files_text, out, f"chunk {i}/{len(chunks)}", ch.truncated)
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
