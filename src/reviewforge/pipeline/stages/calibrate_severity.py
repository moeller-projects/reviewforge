"""Stage: ask Pi to recalibrate finding severities using the digest."""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor, as_completed
import json
from typing import Any

from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json, write_json
from ..cache import cache_key, load_cached_json, store_cached_json
from ..stage import Stage, StageContext
from ..validation import StageLabel, validate_stage


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


def _validated_calibration(doc: Any, original: dict[str, Any]) -> dict[str, Any]:
    """Keep the verified finding when Pi returns malformed calibration JSON."""
    findings = doc.get("findings") if isinstance(doc, dict) else None
    if not isinstance(findings, list) or len(findings) != 1:
        _log("malformed calibration output; preserving verified finding")
        return original
    try:
        validate_stage(
            {"summary": doc.get("summary", ""), "findings": findings},
            StageLabel.SEVERITY_CALIBRATION,
        )
    except SystemExit:
        _log("malformed calibration finding; preserving verified finding")
        return original
    return findings[0]


class CalibrateSeverityStage(Stage):
    name = "calibrate_severity"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        _log("running severity calibration stage")
        verified_doc = read_json(ctx.artifacts.verified) if ctx.artifacts.verified.exists() else {}
        metadata = read_json(ctx.artifacts.metadata) if ctx.artifacts.metadata.exists() else {}
        cache = cache_key([
            "severity_calibration",
            cfg.severity_prompt_path.as_posix(),
            metadata.get("sourceCommit"),
            metadata.get("targetCommit"),
            metadata.get("lastMergeSourceCommit"),
            verified_doc or {},
            ctx.files_text,
            ctx.state.diff_text if ctx.state else "",
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
        ])
        cached = load_cached_json(cfg, "severity_calibration", cache)
        if cached:
            _log("severity calibration cache hit")
            write_json(ctx.artifacts.severity, cached)
            ctx.severity = cached
            return {"findings": len(cached.get("findings", [])), "cached": True}
        text = (
            stage_instruction(
                "severity calibration",
                cfg,
                ctx.artifacts.metadata,
                ctx.files_text,
                ctx.extras.get("wi_context", []),
                ctx.extras.get("thread_context", []),
                ctx.extras.get("paths", {}),
            )
            + (ctx.state.diff_text if ctx.state else "")
        )
        verified_findings = verified_doc.get("findings", []) if isinstance(verified_doc, dict) else []
        if len(verified_findings) <= 1:
            ctx.pi.run_json(cfg.severity_prompt_path, text, ctx.artifacts.severity, "severity calibration")
            ctx.last_token_usage = ctx.pi.last_tokens
            raw_doc = read_json(ctx.artifacts.severity) or {"summary": "", "findings": []}
            if len(verified_findings) == 1:
                doc = {
                    "summary": raw_doc.get("summary", "") if isinstance(raw_doc, dict) else "",
                    "findings": [
                        _validated_calibration(raw_doc, verified_findings[0])
                    ],
                }
            else:
                doc = raw_doc
            validate_stage(doc, StageLabel.SEVERITY_CALIBRATION)
            write_json(ctx.artifacts.severity, doc)
            ctx.severity = doc
            store_cached_json(cfg, "severity_calibration", cache, doc)
            return {"findings": len(doc.get("findings", []))}

        _log(f"calibrating {len(verified_findings)} findings in parallel batches")
        def run_one(idx: int, finding: dict[str, Any]) -> dict[str, Any]:
            out = ctx.artifacts.dir / "raw" / f"severity-{idx}.json"
            payload = text + "\n\nFINDING:\n" + json.dumps(finding, ensure_ascii=False, sort_keys=True)
            if type(ctx.pi).__name__ == "PiRunner":
                # Pi sessions are not safe for concurrent writes. Keep the
                # parallel workers independent while retaining session reuse
                # within each worker's subprocess lifecycle.
                session_id = f"{ctx.pi.session_id}-severity-{idx}"
                runner_cfg = ctx.pi.cfg.with_overrides(pi_session_id=session_id)
                runner = type(ctx.pi)(runner_cfg)
            else:
                runner = ctx.pi
            runner.run_json(cfg.severity_prompt_path, payload, out, f"severity calibration {idx}")
            return read_json(out) or {}

        merged: list[dict[str, Any]] = []
        summary_parts: list[str] = []
        import os
        max_workers = max(1, min(len(verified_findings), max(2, (os.cpu_count() or 2) // 2), 8))
        with ThreadPoolExecutor(max_workers=max_workers) as pool:
            futures = {pool.submit(run_one, i, f): i for i, f in enumerate(verified_findings, 1)}
            for fut in as_completed(futures):
                idx = futures[fut]
                raw_doc = fut.result()
                doc = raw_doc if isinstance(raw_doc, dict) else {}
                if doc.get("summary"):
                    summary_parts.append(doc["summary"])
                merged.append(_validated_calibration(doc, verified_findings[idx - 1]))
        doc = {"summary": " ".join(summary_parts).strip(), "findings": merged}
        validate_stage(doc, StageLabel.SEVERITY_CALIBRATION)
        write_json(ctx.artifacts.severity, doc)
        ctx.severity = doc
        store_cached_json(cfg, "severity_calibration", cache, doc)
        return {"findings": len(doc.get("findings", [])), "batched": True}


__all__ = ["CalibrateSeverityStage"]
