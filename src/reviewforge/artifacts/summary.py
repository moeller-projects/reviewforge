"""Run summary generation.

Produces ``run-summary.json`` with high-level diagnostics for a single run.
Sensitive values (tokens, env) are never included.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from ..config import Config
from .builder import read_json
from .manager import Artifacts


@dataclass
class StageRecord:
    """One entry in :data:`RunSummary.stages`."""

    name: str
    status: str
    started_at: str
    duration_ms: int
    details: dict[str, Any] = field(default_factory=dict)
    token_usage: dict[str, int] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "status": self.status,
            "started_at": self.started_at,
            "duration_ms": self.duration_ms,
            "details": self.details,
            "token_usage": self.token_usage,
        }


@dataclass
class RunSummary:
    """Aggregated diagnostics for a single review run."""

    pr_id: str
    run_id: str
    started_at: str
    finished_at: str
    duration_ms: int
    dry_run: bool
    pi_model: str
    stages: list[StageRecord] = field(default_factory=list)
    finding_counts: dict[str, int] = field(default_factory=dict)
    posted: dict[str, int] = field(default_factory=dict)
    skipped_reason: str | None = None
    exit_code: int = 0
    artifact_dir: str = ""
    review_language: str = ""
    # Runtime metrics are distinct from model-authored review metrics.
    pi_session_id: str | None = None
    pi_session_enabled: bool = True
    pi_session_cleared: bool = False
    invocation_count: int = 0
    repair_invocation_count: int = 0
    reasoning_duration_ms: int = 0
    projection_duration_ms: int = 0
    validation_duration_ms: int = 0
    token_usage: dict[str, int] = field(default_factory=dict)
    anchor_downgraded: int = 0
    anchor_dropped: int = 0

    def add_stage(self, rec: StageRecord) -> None:
        self.stages.append(rec)

    def to_dict(self) -> dict[str, Any]:
        return {
            "pr_id": self.pr_id,
            "run_id": self.run_id,
            "started_at": self.started_at,
            "finished_at": self.finished_at,
            "duration_ms": self.duration_ms,
            "dry_run": self.dry_run,
            "pi_model": self.pi_model,
            "stages": [s.to_dict() for s in self.stages],
            "finding_counts": self.finding_counts,
            "posted": self.posted,
            "skipped_reason": self.skipped_reason,
            "exit_code": self.exit_code,
            "artifact_dir": self.artifact_dir,
            "review_language": self.review_language,
            "pi_session_id": self.pi_session_id,
            "pi_session_enabled": self.pi_session_enabled,
            "pi_session_cleared": self.pi_session_cleared,
            "invocation_count": self.invocation_count,
            "repair_invocation_count": self.repair_invocation_count,
            "reasoning_duration_ms": self.reasoning_duration_ms,
            "projection_duration_ms": self.projection_duration_ms,
            "validation_duration_ms": self.validation_duration_ms,
            "token_usage": self.token_usage,
            "anchor_downgraded": self.anchor_downgraded,
            "anchor_dropped": self.anchor_dropped,
        }


def _iso_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def new_run_summary(cfg: Config, artifacts: Artifacts) -> RunSummary:
    """Build a fresh :class:`RunSummary` for the current run."""
    from ..ai.runner import _default_session_id
    return RunSummary(
        pr_id=cfg.pr_id,
        run_id=artifacts.run_id,
        started_at=_iso_now(),
        finished_at="",
        duration_ms=0,
        dry_run=cfg.dry_run,
        pi_model=cfg.pi_model,
        artifact_dir=str(artifacts.dir),
        review_language=cfg.review_language,
        pi_session_id=cfg.pi_session_id or _default_session_id(cfg),
        pi_session_enabled=cfg.pi_session_enabled,
        pi_session_cleared=cfg.pi_session_clear,
    )


def _safe_count_findings(path: Path) -> int:
    """Read a findings JSON file and return ``len(findings)`` if possible."""
    if not path.exists():
        return 0
    try:
        doc = read_json(path)
    except Exception:
        return 0
    if isinstance(doc, dict):
        findings = doc.get("findings")
        if isinstance(findings, list):
            return len(findings)
    return 0


def _stage_finding_counts(summary: RunSummary) -> dict[str, int]:
    for rec in summary.stages:
        if not isinstance(rec.details, dict):
            continue
        counts = rec.details.get("finding_counts")
        if isinstance(counts, dict):
            return {
                "candidate": int(counts.get("candidate", 0) or 0),
                "verified": int(counts.get("verified", 0) or 0),
                "severity": int(counts.get("severity", 0) or 0),
                "final": int(counts.get("final", 0) or 0),
            }
    return {}


def _set_duration(summary: RunSummary) -> None:
    if not summary.started_at or not summary.finished_at:
        return
    try:
        start = datetime.fromisoformat(summary.started_at)
        end = datetime.fromisoformat(summary.finished_at)
    except ValueError:
        summary.duration_ms = 0
        return
    summary.duration_ms = max(0, int((end - start).total_seconds() * 1000))


def _token_usage(summary: RunSummary) -> dict[str, int]:
    total_in = total_out = 0
    found = False
    for rec in summary.stages:
        usage = rec.token_usage or {}
        total_in += int(usage.get("in", 0) or 0)
        total_out += int(usage.get("out", 0) or 0)
        found |= bool(usage.get("in") or usage.get("out"))
    return {"in": total_in, "out": total_out, "total": total_in + total_out} if found else {}


def _runtime_metrics(summary: RunSummary) -> tuple[int, int, int, int, int]:
    totals = [0, 0, 0, 0, 0]
    for rec in summary.stages:
        metrics = rec.details.get("metrics") if isinstance(rec.details, dict) else None
        if not isinstance(metrics, dict):
            continue
        for index, key in enumerate(
            ("invocationCount", "repairInvocationCount", "reasoningDurationMs", "projectionDurationMs", "validationDurationMs")
        ):
            totals[index] += int(metrics.get(key, 0) or 0)
    return tuple(totals)  # type: ignore[return-value]


def _anchor_counts(summary: RunSummary) -> tuple[int, int]:
    downgraded = dropped = 0
    for rec in summary.stages:
        if rec.name != "validate_anchors":
            continue
        downgraded += int(rec.details.get("downgraded", 0) or 0)
        dropped += int(rec.details.get("dropped", 0) or 0)
    return downgraded, dropped


def finalize_run_summary(
    summary: RunSummary,
    *,
    cfg: Config,
    artifacts: Artifacts,
    posted: dict[str, int] | None = None,
    skipped_reason: str | None = None,
    exit_code: int = 0,
) -> dict[str, Any]:
    """Populate aggregate counts and timestamps on ``summary`` and return the dict."""
    summary.finished_at = _iso_now()
    summary.exit_code = exit_code
    if posted is not None:
        summary.posted = posted
    if skipped_reason is not None:
        summary.skipped_reason = skipped_reason
    _set_duration(summary)
    stage_counts = _stage_finding_counts(summary)
    summary.finding_counts = {
        key: _safe_count_findings(path) or stage_counts.get(key, 0)
        for key, path in {
            "candidate": artifacts.candidate,
            "verified": artifacts.verified,
            "severity": artifacts.severity,
            "final": artifacts.final,
        }.items()
    }
    (
        summary.invocation_count,
        summary.repair_invocation_count,
        summary.reasoning_duration_ms,
        summary.projection_duration_ms,
        summary.validation_duration_ms,
    ) = _runtime_metrics(summary)
    summary.anchor_downgraded, summary.anchor_dropped = _anchor_counts(summary)
    summary.token_usage = _token_usage(summary)
    return summary.to_dict()


__all__ = ["RunSummary", "StageRecord", "build_run_summary", "finalize_run_summary", "new_run_summary"]


# Backward-compat alias for older callers.
def build_run_summary(*args: Any, **kwargs: Any) -> dict[str, Any]:
    """Deprecated: use :func:`finalize_run_summary` instead."""
    return finalize_run_summary(*args, **kwargs)
