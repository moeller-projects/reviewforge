"""Compatibility wrapper: ``ReviewContext`` used by the legacy orchestrator."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any
import sys


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


@dataclass
class ReviewContext:
    """Per-run context shared between stages (legacy interface).

    New code should prefer :class:`auto_pr_reviewer.pipeline.stage.StageContext`.
    This class is kept so the existing tests keep working and so callers that
    have a long-lived reference to ``ctx.state``, ``ctx.artifacts`` etc. can
    still rely on those attributes being set.
    """

    cfg: Any
    artifacts: Any
    pi: Any
    state: Any = None
    files_text: str = ""
    system_prompt: str = ""
    artifact_tmp: Path | None = None
    wi_context: list = field(default_factory=list)
    wi_comments_context: list = field(default_factory=list)
    thread_context: list = field(default_factory=list)
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

    def log(self, message: str) -> None:
        _log(message)

    def paths(self) -> dict[str, Path]:
        return {
            "intent": self.artifacts.intent,
            "plan": self.artifacts.plan,
            "collected": self.artifacts.collected,
            "digest": self.artifacts.digest,
            "candidate": self.artifacts.candidate,
            "verified": self.artifacts.verified,
            "severity": self.artifacts.severity,
            "final": self.artifacts.final,
        }


__all__ = ["ReviewContext"]
