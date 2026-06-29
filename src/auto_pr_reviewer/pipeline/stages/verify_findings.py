"""Stage: adversarially verify candidate findings (drop false positives)."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
import json
import shutil
from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json, write_json
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


class VerifyFindingsStage(Stage):
    name = "verify_findings"

    def should_run(self, ctx: StageContext) -> bool:
        return True

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        if not cfg.verify_findings:
            _log("VERIFY_FINDINGS=0; skipping verification stage")
            shutil.copyfile(ctx.artifacts.candidate, ctx.artifacts.verified)
            doc = read_json(ctx.artifacts.verified) or {"summary": "", "findings": []}
            ctx.verified = doc
            return {"findings": len(doc.get("findings", [])), "skipped": True}
        _log("running adversarial finding verification stage")
        candidate_doc = read_json(ctx.artifacts.candidate) if ctx.artifacts.candidate.exists() else {}
        metadata = read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {}
        cache = cache_key([
            "verify_findings",
            cfg.verify_prompt_path.as_posix(),
            metadata.get("sourceCommit"),
            metadata.get("targetCommit"),
            metadata.get("lastMergeSourceCommit"),
            candidate_doc or {},
            ctx.files_text,
            ctx.state.diff_text if ctx.state else "",
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
        ])
        cached = load_cached_json(cfg, "verify_findings", cache)
        if cached:
            _log("verify findings cache hit")
            write_json(ctx.artifacts.verified, cached)
            ctx.verified = cached
            return {"findings": len(cached.get("findings", [])), "cached": True}
        text = (
            stage_instruction(
                "finding verification",
                cfg,
                ctx.artifacts.metadata,
                ctx.files_text,
                ctx.extras.get("wi_context", []),
                ctx.extras.get("thread_context", []),
                ctx.extras.get("paths", {}),
            )
            + (ctx.state.diff_text if ctx.state else "")
        )
        candidate_findings = candidate_doc.get("findings", []) if isinstance(candidate_doc, dict) else []
        if len(candidate_findings) <= 1:
            ctx.pi.run_json(cfg.verify_prompt_path, text, ctx.artifacts.verified, "finding verification")
            ctx.last_token_usage = ctx.pi.last_tokens
            doc = read_json(ctx.artifacts.verified) or {"summary": "", "findings": []}
            validate_stage(doc, StageLabel.FINDING_VERIFICATION)
            ctx.verified = doc
            store_cached_json(cfg, "verify_findings", cache, doc)
            return {"findings": len(doc.get("findings", []))}

        _log(f"verifying {len(candidate_findings)} findings in parallel batches")
        def run_one(idx: int, finding: dict[str, Any]) -> dict[str, Any]:
            out = ctx.artifacts.dir / "raw" / f"verify-{idx}.json"
            payload = text + "\n\nFINDING:\n" + json.dumps(finding, ensure_ascii=False, sort_keys=True)
            runner = type(ctx.pi)(ctx.pi.cfg) if type(ctx.pi).__name__ == "PiRunner" else ctx.pi
            runner.run_json(cfg.verify_prompt_path, payload, out, f"finding verification {idx}")
            return read_json(out) or {}

        merged: list[dict[str, Any]] = []
        summary_parts: list[str] = []
        import os
        max_workers = max(1, min(len(candidate_findings), max(2, (os.cpu_count() or 2) // 2), 8))
        with ThreadPoolExecutor(max_workers=max_workers) as pool:
            futures = {pool.submit(run_one, i, f): i for i, f in enumerate(candidate_findings, 1)}
            for fut in as_completed(futures):
                doc = fut.result()
                if doc.get("summary"):
                    summary_parts.append(doc.get("summary", ""))
                merged.extend(doc.get("findings", []))
        doc = {"summary": " ".join(summary_parts).strip(), "findings": merged}
        validate_stage(doc, StageLabel.FINDING_VERIFICATION)
        write_json(ctx.artifacts.verified, doc)
        ctx.verified = doc
        store_cached_json(cfg, "verify_findings", cache, doc)
        return {"findings": len(doc.get("findings", [])), "batched": True}


__all__ = ["VerifyFindingsStage"]
