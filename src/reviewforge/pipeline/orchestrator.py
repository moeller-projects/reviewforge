"""Top-level pipeline orchestration.

The orchestrator wires up :class:`Config`, :class:`Artifacts`,
:class:`PiRunner`, and a list of :class:`Stage` instances into a single
``run`` call. It records each stage's outcome in a :class:`RunSummary`,
redacts secrets from the summary, and writes ``run-summary.json``.

Three public entrypoints:

* :func:`run` — full review pipeline (review + post).
* :func:`run_review_only` — review only, no posting.
* :func:`run_post_only` — post a previously generated review.

A few legacy helpers (``should_skip``, ``ensure_tools``) remain for older
callers that imported them directly.
"""
from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass, field
from dataclasses import replace as dataclass_replace
from pathlib import Path
from typing import Any

from ..ado.client import get_pr
from ..ai.model_runner import ModelRunner, create_model_runner
from ..artifacts.builder import changed_files, read_json, write_json
from ..artifacts.manager import Artifacts, create as create_artifacts
from ..artifacts.summary import (
    RunSummary,
    StageRecord,
    finalize_run_summary,
    new_run_summary,
)
from ..runlog import configure as configure_runlog, info as log_info
from ..config import Config
from ..exceptions import DependencyError, InputError
from ..git import ops as git_ops
from ..pipeline.context import ReviewContext
from .stage import Stage, StageContext, run_stages
from .stages import (
    DEFAULT_PIPELINE,
    POST_ONLY_PIPELINE,
    REPLY_PIPELINE,
    REVIEW_ONLY_PIPELINE,
)
from .validation import validate_postable_review_doc
_log = log_info





# ---------------------------------------------------------------------------
# Legacy helpers (preserved for back-compat with existing tests)
# ---------------------------------------------------------------------------


def ensure_tools(cfg: Config | None = None) -> None:
    """Raise a domain error if the selected backend's tool is unavailable."""
    backend_tools = {"pi": "pi"}
    backend = cfg.model_backend if cfg is not None else "pi"
    tool = backend_tools.get(backend)
    if tool is None:
        raise DependencyError(f"[review][ERROR] unknown model backend: {backend}")
    for required in ("git", tool, "rg"):
        if not shutil.which(required):
            raise DependencyError(
                f"[review][ERROR] {required} required", details={"tool": required}
            )


def _branch_skip(cfg: Config, metadata: dict[str, Any]) -> dict[str, Any] | None:
    if not cfg.review_target_branches:
        return None
    allowed = {
        x.strip().removeprefix("refs/heads/")
        for x in cfg.review_target_branches.split(",")
        if x.strip()
    }
    target = str(metadata.get("targetRefName") or "").removeprefix("refs/heads/")
    if target and allowed and target not in allowed:
        return {
            "summary": f"Skipped: target branch {target!r} not in review policy {sorted(allowed)}.",
            "findings": [],
        }
    return None


def should_skip(cfg: Config, metadata: dict[str, Any]) -> dict[str, Any] | None:
    """Return a skip reason dict (or ``None``) for the current PR."""
    if cfg.force_review:
        return None
    if metadata.get("isDraft") is True:
        return {"summary": "Skipped: PR is draft.", "findings": []}
    if (metadata.get("status") or "active") != "active":
        return {"summary": f"Skipped: PR status {metadata.get('status')}.", "findings": []}
    return _branch_skip(cfg, metadata)


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
    pi = create_model_runner(cfg)
    return ReviewContext(cfg=cfg, artifacts=artifacts, pi=pi)


def _make_stage_context(
    cfg: Config,
    artifacts: Artifacts,
    pi: ModelRunner,
) -> StageContext:
    """Build a fresh :class:`StageContext` for canonical review results."""
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=pi)
    ctx.extras["paths"] = {
        "final": artifacts.final,
        "review_result": artifacts.review_result,
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
    cfg.validate_files(include_reply_prompt=cfg.reply_comments)
    artifacts = create_artifacts(cfg)
    configure_runlog(artifacts.run_log)
    log_info("run started")
    pi = create_model_runner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    try:
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
    finally:
        _write_pi_invocations(ctx)
        _cleanup_repo_state(ctx)


def run_review_only(cfg: Config, *, output: Path | None = None) -> RunOutcome:
    """Run the review pipeline without posting. Returns findings in the summary.

    If ``output`` is provided, the final review doc is also copied there.
    """
    cfg.validate_files()
    artifacts = create_artifacts(cfg)
    configure_runlog(artifacts.run_log)
    log_info("review-only run started")
    pi = create_model_runner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    try:
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
    finally:
        _write_pi_invocations(ctx)
        _cleanup_repo_state(ctx)


def run_post_only(cfg: Config, *, input_path: Path) -> RunOutcome:
    """Post a previously generated review doc to ADO.

    ``input_path`` must point at a JSON file shaped like ``final-findings.json``.
    """
    cfg.validate_files()
    if not input_path.exists():
        raise InputError(
            f"[review][ERROR] input file not found: {input_path}",
            details={"input_path": str(input_path)},
        )
    artifacts = create_artifacts(cfg)
    configure_runlog(artifacts.run_log)
    log_info("post-only run started")
    pi = create_model_runner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)

    payload = read_json(input_path) or {"summary": "", "findings": []}
    validate_postable_review_doc(payload)
    ctx.final = payload

    try:
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
    finally:
        _cleanup_repo_state(ctx)



def run_reply_only(cfg: Config) -> RunOutcome:
    """Answer pending human replies on bot threads without new findings.

    Forces full-review mode so the repository checkout is prepared even when
    review-mode detection would consider the PR unchanged.
    """
    cfg = dataclass_replace(cfg, force_full_review=True)
    cfg.validate_files(include_reply_prompt=cfg.reply_comments)
    artifacts = create_artifacts(cfg)
    configure_runlog(artifacts.run_log)
    log_info("reply-only run started")
    pi = create_model_runner(cfg)
    summary = new_run_summary(cfg, artifacts)
    ctx = _make_stage_context(cfg, artifacts, pi)
    ctx.extras["explicit_reply_command"] = True

    try:
        results = run_stages(REPLY_PIPELINE, ctx)
        _record_results(summary, results)
        exit_code = _exit_code_for(results)
        finalize = finalize_run_summary(
            summary,
            cfg=cfg,
            artifacts=artifacts,
            posted={"reply_only": 1, "created": 0, "skipped": 0},
            skipped_reason=ctx.skip_reason,
            exit_code=exit_code,
        )
        write_json(artifacts.summary, finalize)
        return RunOutcome(exit_code=exit_code, summary=summary, stages=results)
    finally:
        _write_pi_invocations(ctx)
        _cleanup_repo_state(ctx)


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------


def _record_results(summary: RunSummary, results: list) -> None:
    for r in results:
        reason = getattr(r, "reason", None)
        if r.status == "skipped":
            _log(f"stage {r.name} skipped: {reason}")
        else:
            _log(f"stage {r.name} {r.status} in {r.duration_ms}ms")
        details = dict(r.details or {})
        if r.error:
            details.setdefault("error", r.error)
        if reason:
            details.setdefault("reason", reason)
        summary.add_stage(
            StageRecord(
                name=r.name,
                status=r.status,
                started_at=r.started_at,
                duration_ms=r.duration_ms,
                details=details,
                token_usage=getattr(r, "token_usage", {}) or {},
            )
        )


def _exit_code_for(results: list) -> int:
    """Return ``1`` if any stage failed, else ``0``."""
    return 1 if any(r.status == "failed" for r in results) else 0


def _cleanup_repo_state(ctx: StageContext) -> None:
    state = getattr(ctx, "state", None)
    if state is None:
        return
    try:
        git_ops.cleanup(state)
    except Exception as exc:  # pragma: no cover - best effort cleanup
        log_info(f"repository cleanup failed: {type(exc).__name__}: {exc}")


def _write_pi_invocations(ctx: StageContext) -> None:
    """Persist per-invocation Pi outcome records to ``pi-invocations.json``."""
    pi = getattr(ctx, "pi", None)
    if pi is None:
        return
    invocations = getattr(pi, "invocations", None)
    if not isinstance(invocations, list) or not invocations:
        return
    try:
        write_json(ctx.artifacts.pi_invocations, invocations)
    except Exception as exc:  # noqa: BLE001 - best-effort diagnostics
        log_info(f"failed to write pi-invocations.json: {type(exc).__name__}: {exc}")

__all__ = [
    "RunOutcome",
    "ensure_tools",
    "run",
    "run_full",
    "run_post_only",
    "run_reply_only",
    "run_review_only",
    "should_skip",
]
