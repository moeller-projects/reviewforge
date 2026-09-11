"""Stage: adversarially verify candidate findings (drop false positives)."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
import json
import os
import shutil
import sys
import traceback
from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json, write_json
from ...runlog import info as _log
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _finding_titles(findings: list[dict[str, Any]]) -> list[str]:
    return [finding["title"].strip() for finding in findings if isinstance(finding.get("title"), str) and finding["title"].strip()]


def _log_validation_failure(stage: str, output: Any, doc: Any, *, error: BaseException) -> None:
    findings = doc.get("findings", []) if isinstance(doc, dict) else []
    _log(f"{stage} validation failed: output={output}; findings={len(findings)}; title_count={len(_finding_titles(findings))}; titles={_finding_titles(findings)}; error={type(error).__name__}")
    print(f"[review][DEBUG] {stage} validation payload: {json.dumps(doc, ensure_ascii=False, sort_keys=True)}", file=sys.stderr)


def _log_worker_crash(idx: int, output: Any, finding: dict[str, Any], exc: BaseException) -> None:
    _log(f"finding verification {idx} crashed: output={output}; title={str(finding.get('title', '')).strip()!r}; error={type(exc).__name__}: {exc}")
    print(f"[review][DEBUG] finding verification {idx} payload: {json.dumps(finding, ensure_ascii=False, sort_keys=True)}", file=sys.stderr)
    traceback.print_exception(type(exc), exc, exc.__traceback__, file=sys.stderr)


def _verification_cache(ctx: StageContext, cfg: Any, candidate: Any, metadata: Any) -> str:
    return cache_key([
        "verify_findings", cfg.verify_prompt_path.as_posix(), metadata.get("sourceCommit"), metadata.get("targetCommit"), metadata.get("lastMergeSourceCommit"), candidate or {}, ctx.files_text, ctx.state.diff_text if ctx.state else "", ctx.extras.get("wi_context", []), ctx.extras.get("thread_context", []),
    ])


def _verification_text(ctx: StageContext, cfg: Any) -> str:
    return stage_instruction("finding verification", cfg, ctx.artifacts.metadata, ctx.files_text, ctx.extras.get("wi_context", []), ctx.extras.get("thread_context", []), ctx.extras.get("paths", {})) + (ctx.state.diff_text if ctx.state else "")


def _verify_one(ctx: StageContext, cfg: Any, text: str, idx: int, finding: dict[str, Any]) -> tuple[int, dict[str, Any], dict[str, int]]:
    out = ctx.artifacts.dir / "raw" / f"verify-{idx}.json"
    runner = ctx.pi
    if type(ctx.pi).__name__ in {"PiCliRunner", "PiRunner"}:
        runner = type(ctx.pi)(ctx.pi.cfg.with_overrides(pi_session_id=f"{ctx.pi.session_id}-verify-{idx}"))
    try:
        runner.run_json(cfg.verify_prompt_path, text + "\n\nFINDING:\n" + json.dumps(finding, ensure_ascii=False, sort_keys=True), out, f"finding verification {idx}")
        usage = getattr(runner, "last_tokens", {}) or {}
        return idx, read_json(out) or {}, {key: int(usage.get(key, 0) or 0) for key in ("in", "out", "total")}
    except BaseException as exc:
        _log_worker_crash(idx, out, finding, exc)
        raise


def _merge_verified(ctx: StageContext, docs: dict[int, dict[str, Any]], findings: list[dict[str, Any]], tokens: dict[str, int]) -> dict[str, Any]:
    summary_parts, merged = [], []
    for idx in sorted(docs):
        doc = docs[idx]
        if doc.get("summary"):
            summary_parts.append(doc["summary"])
        merged.extend(doc.get("findings", []))
    existing = ctx.extras.get("_worker_token_usage")
    if not isinstance(existing, dict):
        existing = {"in": 0, "out": 0, "total": 0}
    for key in tokens:
        existing[key] = int(existing.get(key, 0) or 0) + tokens[key]
    ctx.extras["_worker_token_usage"] = existing
    return {"summary": " ".join(summary_parts).strip(), "findings": merged}


def _run_verified_batch(ctx: StageContext, cfg: Any, text: str, findings: list[dict[str, Any]]) -> dict[str, Any]:
    _log(f"verifying {len(findings)} findings in parallel batches")
    workers = max(1, min(len(findings), max(2, (os.cpu_count() or 2) // 2), 8))
    docs, tokens = {}, {"in": 0, "out": 0, "total": 0}
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(_verify_one, ctx, cfg, text, i, finding) for i, finding in enumerate(findings, 1)]
        for future in as_completed(futures):
            idx, doc, usage = future.result()
            docs[idx] = doc
            for key in tokens:
                tokens[key] += usage[key]
    return _merge_verified(ctx, docs, findings, tokens)


def _validate_verified(ctx: StageContext, doc: Any, output: Any, stage: str = "finding verification") -> None:
    try:
        validate_stage(doc, StageLabel.FINDING_VERIFICATION)
    except BaseException as exc:
        _log_validation_failure(stage, output, doc, error=exc)
        raise


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
        candidate = read_json(ctx.artifacts.candidate) if ctx.artifacts.candidate.exists() else {}
        metadata = read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {}
        cache = _verification_cache(ctx, cfg, candidate, metadata)
        if cached := load_cached_json(cfg, "verify_findings", cache):
            _log("verify findings cache hit")
            write_json(ctx.artifacts.verified, cached)
            ctx.verified = cached
            return {"findings": len(cached.get("findings", [])), "cached": True}
        text = _verification_text(ctx, cfg)
        findings = candidate.get("findings", []) if isinstance(candidate, dict) else []
        if len(findings) <= 1:
            ctx.pi.run_json(cfg.verify_prompt_path, text, ctx.artifacts.verified, "finding verification")
            ctx.last_token_usage = ctx.pi.last_tokens
            doc = read_json(ctx.artifacts.verified) or {"summary": "", "findings": []}
            _validate_verified(ctx, doc, ctx.artifacts.verified)
        else:
            ctx.artifacts.raw_dir.mkdir(parents=True, exist_ok=True)
            doc = _run_verified_batch(ctx, cfg, text, findings)
            _validate_verified(ctx, doc, ctx.artifacts.verified, "merged finding verification")
        write_json(ctx.artifacts.verified, doc)
        ctx.verified = doc
        store_cached_json(cfg, "verify_findings", cache, doc)
        return {"findings": len(doc.get("findings", [])), **({"batched": True} if len(findings) > 1 else {})}


__all__ = ["VerifyFindingsStage"]
