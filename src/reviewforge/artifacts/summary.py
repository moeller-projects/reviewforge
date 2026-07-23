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
    if summary.started_at and summary.finished_at:
        try:
            start = datetime.fromisoformat(summary.started_at)
            end = datetime.fromisoformat(summary.finished_at)
            summary.duration_ms = max(0, int((end - start).total_seconds() * 1000))
        except ValueError:
            summary.duration_ms = 0

    summary.finding_counts = {
        "candidate": _safe_count_findings(artifacts.candidate),
        "verified": _safe_count_findings(artifacts.verified),
        "severity": _safe_count_findings(artifacts.severity),
        "final": _safe_count_findings(artifacts.final),
    }
    # Aggregate token usage across all stage records.
    total_in = 0
    total_out = 0
    any_tokens = False
    for rec in summary.stages:
        tu = rec.token_usage or {}
        if tu.get("in"):
            total_in += int(tu["in"])
            any_tokens = True
        if tu.get("out"):
            total_out += int(tu["out"])
            any_tokens = True
    summary.invocation_count = 0
    summary.repair_invocation_count = 0
    summary.reasoning_duration_ms = 0
    summary.projection_duration_ms = 0
    summary.validation_duration_ms = 0
    for rec in summary.stages:
        metrics = rec.details.get("metrics") if isinstance(rec.details, dict) else None
        if isinstance(metrics, dict):
            summary.invocation_count += int(metrics.get("invocationCount", 0) or 0)
            summary.repair_invocation_count += int(
                metrics.get("repairInvocationCount", 0) or 0
            )
            summary.reasoning_duration_ms += int(
                metrics.get("reasoningDurationMs", 0) or 0
            )
            summary.projection_duration_ms += int(
                metrics.get("projectionDurationMs", 0) or 0
            )
            summary.validation_duration_ms += int(
                metrics.get("validationDurationMs", 0) or 0
            )
    for rec in summary.stages:
        if rec.name == "validate_anchors":
            summary.anchor_downgraded += int(rec.details.get("downgraded", 0) or 0)
            summary.anchor_dropped += int(rec.details.get("dropped", 0) or 0)
    if any_tokens:
        summary.token_usage = {
            "in": total_in,
            "out": total_out,
            "total": total_in + total_out,
        }
    return summary.to_dict()


__all__ = ["RunSummary", "StageRecord", "build_run_summary", "finalize_run_summary", "new_run_summary"]


# Backward-compat alias for older callers.
def build_run_summary(*args: Any, **kwargs: Any) -> dict[str, Any]:
    """Deprecated: use :func:`finalize_run_summary` instead."""
    return finalize_run_summary(*args, **kwargs)
