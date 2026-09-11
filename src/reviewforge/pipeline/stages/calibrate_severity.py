"""Stage: ask Pi to recalibrate finding severities using the digest."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
import json
import os
from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json, write_json
from ...exceptions import SchemaValidationError
from ...runlog import info as _log
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _validated_calibration(doc: Any, original: dict[str, Any]) -> dict[str, Any]:
    findings = doc.get("findings") if isinstance(doc, dict) else None
    if not isinstance(findings, list) or len(findings) != 1:
        _log("malformed calibration output; preserving verified finding")
        return original
    try:
        validate_stage({"summary": doc.get("summary", ""), "findings": findings}, StageLabel.SEVERITY_CALIBRATION)
    except SchemaValidationError:
        _log("malformed calibration finding; preserving verified finding")
        return original
    return findings[0]


def _calibration_cache(ctx: StageContext, cfg: Any, verified: Any, metadata: Any) -> str:
    return cache_key([
        "severity_calibration", cfg.severity_prompt_path.as_posix(),
        metadata.get("sourceCommit"), metadata.get("targetCommit"), metadata.get("lastMergeSourceCommit"),
        verified or {}, ctx.files_text, ctx.state.diff_text if ctx.state else "",
        ctx.extras.get("wi_context", []), ctx.extras.get("thread_context", []),
    ])


def _calibration_text(ctx: StageContext, cfg: Any) -> str:
    return stage_instruction(
        "severity calibration", cfg, ctx.artifacts.metadata, ctx.files_text,
        ctx.extras.get("wi_context", []), ctx.extras.get("thread_context", []), ctx.extras.get("paths", {}),
    ) + (ctx.state.diff_text if ctx.state else "")


def _run_calibration_one(ctx: StageContext, cfg: Any, text: str, idx: int, finding: dict[str, Any]) -> tuple[int, dict[str, Any], dict[str, int]]:
    out = ctx.artifacts.dir / "raw" / f"severity-{idx}.json"
    payload = text + "\n\nFINDING:\n" + json.dumps(finding, ensure_ascii=False, sort_keys=True)
    runner = ctx.pi
    if type(ctx.pi).__name__ in {"PiCliRunner", "PiRunner"}:
        runner = type(ctx.pi)(ctx.pi.cfg.with_overrides(pi_session_id=f"{ctx.pi.session_id}-severity-{idx}"))
    runner.run_json(cfg.severity_prompt_path, payload, out, f"severity calibration {idx}")
    usage = getattr(runner, "last_tokens", {}) or {}
    return idx, read_json(out) or {}, {key: int(usage.get(key, 0) or 0) for key in ("in", "out", "total")}


def _merge_calibrations(ctx: StageContext, docs: dict[int, dict[str, Any]], findings: list[dict[str, Any]], tokens: dict[str, int]) -> dict[str, Any]:
    summaries = []
    merged = []
    for idx in sorted(docs):
        doc = docs[idx]
        if not isinstance(doc, dict):
            doc = {}
        if doc.get("summary"):
            summaries.append(doc["summary"])
        merged.append(_validated_calibration(doc, findings[idx - 1]))
    existing = ctx.extras.get("_worker_token_usage")
    if not isinstance(existing, dict):
        existing = {"in": 0, "out": 0, "total": 0}
    for key in tokens:
        existing[key] = int(existing.get(key, 0) or 0) + tokens[key]
    ctx.extras["_worker_token_usage"] = existing
    return {"summary": " ".join(summaries).strip(), "findings": merged}


def _run_batched(ctx: StageContext, cfg: Any, text: str, findings: list[dict[str, Any]]) -> dict[str, Any]:
    _log(f"calibrating {len(findings)} findings in parallel batches")
    workers = max(1, min(len(findings), max(2, (os.cpu_count() or 2) // 2), 8))
    docs: dict[int, dict[str, Any]] = {}
    tokens = {"in": 0, "out": 0, "total": 0}
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(_run_calibration_one, ctx, cfg, text, i, finding) for i, finding in enumerate(findings, 1)]
        for future in as_completed(futures):
            idx, doc, usage = future.result()
            docs[idx] = doc
            for key in tokens:
                tokens[key] += usage[key]
    return _merge_calibrations(ctx, docs, findings, tokens)


def _calibrate_single(ctx: StageContext, cfg: Any, text: str, findings: list[dict[str, Any]]) -> dict[str, Any]:
    ctx.pi.run_json(cfg.severity_prompt_path, text, ctx.artifacts.severity, "severity calibration")
    ctx.last_token_usage = ctx.pi.last_tokens
    raw = read_json(ctx.artifacts.severity) or {"summary": "", "findings": []}
    if len(findings) != 1:
        return raw
    return {"summary": raw.get("summary", "") if isinstance(raw, dict) else "", "findings": [_validated_calibration(raw, findings[0])]}
class CalibrateSeverityStage(Stage):
    name = "calibrate_severity"


    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        _log("running severity calibration stage")
        verified = read_json(ctx.artifacts.verified) if ctx.artifacts.verified.exists() else {}
        metadata = read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {}
        cache = _calibration_cache(ctx, cfg, verified, metadata)
        cached = load_cached_json(cfg, "severity_calibration", cache)
        if cached:
            _log("severity calibration cache hit")
            write_json(ctx.artifacts.severity, cached)
            ctx.severity = cached
            return {"findings": len(cached.get("findings", [])), "cached": True}
        text = _calibration_text(ctx, cfg)
        findings = verified.get("findings", []) if isinstance(verified, dict) else []
        doc = _calibrate_single(ctx, cfg, text, findings) if len(findings) <= 1 else _run_batched(ctx, cfg, text, findings)
        validate_stage(doc, StageLabel.SEVERITY_CALIBRATION)
        write_json(ctx.artifacts.severity, doc)
        ctx.severity = doc
        store_cached_json(cfg, "severity_calibration", cache, doc)
        return {"findings": len(doc.get("findings", [])), **({"batched": True} if len(findings) > 1 else {})}


__all__ = ["CalibrateSeverityStage"]
