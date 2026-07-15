"""The :class:`Stage` interface and a small runner.

A :class:`Stage` is a single unit of work in the review pipeline. It owns a
name, accepts a :class:`StageContext`, and returns a :class:`StageResult`.
The runner records durations and surfaces failures with clear context.

This module is intentionally minimal so individual stages can stay small and
testable. Each stage is implemented in :mod:`reviewforge.pipeline.stages`.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable
import sys
import time


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


class StageStatus:
    OK = "ok"
    SKIPPED = "skipped"
    FAILED = "failed"


@dataclass
class StageContext:
    """Mutable per-run state passed between stages.

    Stages are expected to read from and write to the attributes they care
    about. The ``extras`` dict is for stage-specific scratch space.
    """

    cfg: Any  # Config
    artifacts: Any  # Artifacts
    state: Any  # RepoState | None
    pi: Any  # PiRunner
    metadata: dict[str, Any] = field(default_factory=dict)
    intent: dict[str, Any] | None = None
    plan: dict[str, Any] | None = None
    collected: dict[str, Any] | None = None
    digest: dict[str, Any] | None = None
    candidate: dict[str, Any] | None = None
    verified: dict[str, Any] | None = None
    severity: dict[str, Any] | None = None
    final: dict[str, Any] | None = None
    posted: dict[str, int] = field(default_factory=dict)
    skip_reason: str | None = None
    extras: dict[str, Any] = field(default_factory=dict)
    #: Token usage reported by the most recent Pi call. Updated by stages
    #: after they call :meth:`PiRunner.run_json`. Aggregated into the
    #: run-summary by the orchestrator.
    last_token_usage: dict[str, int] = field(default_factory=dict)


@dataclass
class StageResult:
    """Outcome of a single stage."""

    name: str
    status: str
    started_at: str
    finished_at: str
    duration_ms: int
    details: dict[str, Any] = field(default_factory=dict)
    error: str | None = None
    token_usage: dict[str, int] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "status": self.status,
            "started_at": self.started_at,
            "finished_at": self.finished_at,
            "duration_ms": self.duration_ms,
            "details": self.details,
            "error": self.error,
            "token_usage": self.token_usage,
        }


class Stage:
    """Base class for pipeline stages.

    Subclasses must implement :meth:`run` and set :attr:`name`. They may
    override :meth:`should_run` to short-circuit the stage (e.g. when its
    input data is missing or ``DRY_RUN`` forbids writes).
    """

    name: str = "stage"

    def should_run(self, ctx: StageContext) -> bool:  # noqa: D401 - simple hook
        return True

    def run(self, ctx: StageContext) -> StageResult:  # pragma: no cover - abstract
        raise NotImplementedError

    def __call__(self, ctx: StageContext) -> StageResult:
        """Execute the stage with timing and error capture."""
        started_at = _now_iso()
        t0 = time.monotonic()
        try:
            if not self.should_run(ctx):
                finished_at = _now_iso()
                return StageResult(
                    name=self.name,
                    status=StageStatus.SKIPPED,
                    started_at=started_at,
                    finished_at=finished_at,
                    duration_ms=int((time.monotonic() - t0) * 1000),
                )
            details = self.run(ctx) or {}
            if not isinstance(details, dict):
                details = {"result": details}
            finished_at = _now_iso()
            # Capture token usage if the stage left it on the context.
            tokens = ctx.last_token_usage
            return StageResult(
                name=self.name,
                status=StageStatus.OK,
                started_at=started_at,
                finished_at=finished_at,
                duration_ms=int((time.monotonic() - t0) * 1000),
                details=details,
                token_usage=dict(tokens),
            )
        except SystemExit as exc:
            finished_at = _now_iso()
            return StageResult(
                name=self.name,
                status=StageStatus.FAILED,
                started_at=started_at,
                finished_at=finished_at,
                duration_ms=int((time.monotonic() - t0) * 1000),
                error=str(exc),
            )
        except Exception as exc:  # noqa: BLE001 - we want to capture everything
            finished_at = _now_iso()
            return StageResult(
                name=self.name,
                status=StageStatus.FAILED,
                started_at=started_at,
                finished_at=finished_at,
                duration_ms=int((time.monotonic() - t0) * 1000),
                error=f"{type(exc).__name__}: {exc}",
            )


def run_stages(stages: list[Stage], ctx: StageContext) -> list[StageResult]:
    """Run ``stages`` in order, stopping at the first failure.

    Returns the list of :class:`StageResult` for every stage that ran,
    including any that were skipped.
    """
    results: list[StageResult] = []
    for stage in stages:
        result = stage(ctx)
        results.append(result)
        if result.status == StageStatus.FAILED:
            print(
                f"[review][ERROR] stage {result.name} failed: {result.error}",
                file=sys.stderr,
            )
            break
    return results


__all__ = [
    "Stage",
    "StageContext",
    "StageResult",
    "StageStatus",
    "run_stages",
]
