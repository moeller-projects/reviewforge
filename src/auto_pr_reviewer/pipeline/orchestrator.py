"""Top-level pipeline orchestration.

The orchestrator wires up :class:`Config`, :class:`Artifacts`,
:class:`PiRunner`, and a list of :class:`Stage` instances into a single
``run`` call. It records each stage's outcome in a :class:`RunSummary`,
redacts secrets from the summary, and writes ``run-summary.json``.

Three public entrypoints:

* :func:`run` — full review pipeline (review + post).
* :func:`run_review_only` — review only, no posting.
* :func:`run_post_only` — post a previously generated review.

A few legacy helpers (``should_skip``, ``ensure_tools``) are preserved for
backward compatibility with the original ``scripts/pipeline/orchestrator.py``.
"""
from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from ..ado.client import call_helper, get_pr
from ..ai.runner import PiRunner
from ..artifacts.builder import changed_files, read_json, write_json
from ..artifacts.manager import Artifacts, create as create_artifacts
from ..artifacts.summary import (
    RunSummary,
    StageRecord,
    finalize_run_summary,
    new_run_summary,
)
from ..config import Config
from ..git import ops as git_ops
from ..pipeline.context import ReviewContext
from .stage import Stage, StageContext, run_stages
from .stages import DEFAULT_PIPELINE, POST_ONLY_PIPELINE, REVIEW_ONLY_PIPELINE
from .validation import validate_review_doc


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


# ---------------------------------------------------------------------------
# Legacy helpers (preserved for back-compat with existing tests)
# ---------------------------------------------------------------------------


def ensure_tools() -> None:
    """Raise ``SystemExit`` if a required tool is missing on ``PATH``."""
    for tool in ("git", "pi", "rg"):
        if not shutil.which(tool):
            raise SystemExit(f"[review][ERROR] {tool} required")


def should_skip(cfg: Config, metadata: dict[str, Any]) -> dict[str, Any] | None:
    """Return a skip reason dict (or ``None``) for the current PR."""
    if cfg.force_review:
        return None
    if metadata.get("isDraft") is True:
        return {"summary": "Skipped: PR is draft.", "findings": []}
    if (metadata.get("status") or "active") != "active":
        return {"summary": f"Skipped: PR status {metadata.get('status')}.", "findings": []}
    if cfg.review_target_branches:
        allowed = {
            x.strip().removeprefix("refs/heads/")
            for x in cfg.review_target_branches.split(",")
            if x.strip()
        }
        target = str(metadata.get("targetRefName") or "").removeprefix("refs/heads/")
        if target and allowed and target not in allowed:
            return {
                "summary": (
                    f"Skipped: target branch {target!r} not in review policy "
                    f"{sorted(allowed)}."
                ),
                "findings": [],
            }
    return None


# ---------------------------------------------------------------------------
# Outcome
# ---------------------------------------------------------------------------


@dataclass
class RunOutcome:
    """Return value of :func:`run` and its variants."""

    exit_code: int
    summary: RunSummary
    stages: list = field(default_factory=list)

    @property
    def success(self) -> bool:
        return self.exit_code == 0


# ---------------------------------------------------------------------------
# Pipeline constructors
# ---------------------------------------------------------------------------


def _build_legacy_context(cfg: Config, artifacts: Artifacts) -> ReviewContext:
    """Build a :class:`ReviewContext` for legacy code paths."""
    pi = PiRunner(cfg)
    return ReviewContext(cfg=cfg, artifacts=artifacts, pi=pi)


def _make_stage_context(
    cfg: Config,
    artifacts: Artifacts,
    pi: PiRunner,
) -> StageContext:
    """Build a fresh :class:`StageContext` populated with the legacy paths."""
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=pi)
    ctx.extras["paths"] = {
        "intent": artifacts.intent,
        "plan": artifacts.plan,
        "collected": artifacts.collected,
        "digest": artifacts.digest,
        "candidate": artifacts.candidate,
        "verified": artifacts.verified,
        "severity": artifacts.severity,
        "final": artifacts.final,
        "metadata": artifacts.metadata,
        "diff": artifacts.diff,
        "work_items": artifacts.work_items,
        "threads": artifacts.threads,
    }
    return ctx


# ---------------------------------------------------------------------------
# Main entrypoints
# ---------------------------------------------------------------------------


def run(cfg: Config) -> int:
    """Legacy entrypoint. Run the full pipeline and return an exit code."""
    return run_full(cfg).exit_code


def run_full(cfg: Config) -> RunOutcome:
    """Run the full review pipeline (review + post)."""
    cfg.validate_files()
    artifacts = create_artifacts(cfg)
    pi = PiRunner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    results = run_stages(DEFAULT_PIPELINE, ctx)
    _record_results(summary, results)
    exit_code = _exit_code_for(results)
    finalize = finalize_run_summary(
        summary,
        cfg=cfg,
        artifacts=artifacts,
        posted=ctx.posted,
        skipped_reason=ctx.skip_reason,
        exit_code=exit_code,
    )
    write_json(artifacts.summary, finalize)
    return RunOutcome(exit_code=exit_code, summary=summary, stages=results)


def run_review_only(cfg: Config, *, output: Path | None = None) -> RunOutcome:
    """Run the review pipeline without posting. Returns findings in the summary.

    If ``output`` is provided, the final review doc is also copied there.
    """
    cfg.validate_files()
    artifacts = create_artifacts(cfg)
    pi = PiRunner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    results = run_stages(REVIEW_ONLY_PIPELINE, ctx)
    _record_results(summary, results)
    exit_code = _exit_code_for(results)
    finalize = finalize_run_summary(
        summary,
        cfg=cfg,
        artifacts=artifacts,
        posted={"review_only": 1, "created": 0, "skipped": 0},
        skipped_reason=ctx.skip_reason,
        exit_code=exit_code,
    )
    write_json(artifacts.summary, finalize)
    if output is not None and artifacts.final.exists():
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(artifacts.final, output)
    return RunOutcome(exit_code=exit_code, summary=summary, stages=results)


def run_post_only(cfg: Config, *, input_path: Path) -> RunOutcome:
    """Post a previously generated review doc to ADO.

    ``input_path`` must point at a JSON file shaped like ``final-findings.json``.
    """
    cfg.validate_files()
    if not input_path.exists():
        raise SystemExit(f"[review][ERROR] input file not found: {input_path}")
    artifacts = create_artifacts(cfg)
    pi = PiRunner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    # Persist the input as both the severity and final docs so PostToAdoStage
    # reads the same shape it expects from the full pipeline.
    payload = read_json(input_path) or {"summary": "", "findings": []}
    validate_review_doc(payload)
    write_json(artifacts.severity, payload)
    write_json(artifacts.final, payload)
    ctx.severity = payload
    ctx.final = payload

    results = run_stages(POST_ONLY_PIPELINE, ctx)
    _record_results(summary, results)
    exit_code = _exit_code_for(results)
    finalize = finalize_run_summary(
        summary,
        cfg=cfg,
        artifacts=artifacts,
        posted=ctx.posted,
        skipped_reason=ctx.skip_reason,
        exit_code=exit_code,
    )
    write_json(artifacts.summary, finalize)
    return RunOutcome(exit_code=exit_code, summary=summary, stages=results)


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------


def _record_results(summary: RunSummary, results: list) -> None:
    for r in results:
        summary.add_stage(
            StageRecord(
                name=r.name,
                status=r.status,
                started_at=r.started_at,
                duration_ms=r.duration_ms,
                details=r.details or {},
                token_usage=getattr(r, "token_usage", {}) or {},
            )
        )


def _exit_code_for(results: list) -> int:
    """Return ``1`` if any stage failed, else ``0``."""
    return 1 if any(r.status == "failed" for r in results) else 0


__all__ = [
    "RunOutcome",
    "ensure_tools",
    "run",
    "run_full",
    "run_post_only",
    "run_review_only",
    "should_skip",
]
