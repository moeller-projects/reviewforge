"""Review-specific tools for the native engine.

Findings arrive as validated tool calls (OCR ``code_comment`` model), not as
one giant JSON document: per-call validation, partial results survive a
crashed loop, and ``task_done`` is the deliberate-clean signal.

File reading itself comes from the harness ``FileSystem`` capability
(``read_only=True``) — this toolset owns only review-domain tools.
Collected state lives on the instance; the engine reads it after the run.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

from pydantic import ValidationError
from pydantic_ai.toolsets import FunctionToolset

from ..pipeline.schemas import (
    Confidence,
    ContextBasis,
    RichFinding,
    Severity,
    Uncertainty,
)
from .prefix import _byte_cap_with_pointer

#: Byte cap for a single ``read_context`` result.
_CONTEXT_READ_MAX_BYTES = 65536

#: Marker returned when the model finishes deliberately.
_TASK_DONE_REPLY = (
    "Review marked complete. Now produce the final narrative JSON "
    "(review_summary, pr_summary, good_practices). Do not repeat findings."
)


def _invalid_name(filename: str) -> bool:
    return not filename or "/" in filename or "\\" in filename or ".." in filename


class ReviewCollectorToolset(FunctionToolset):
    """Tools: ``read_context``, ``record_finding``, ``record_uncertainty``, ``task_done``.

    Mirrors the harness toolset pattern (a leaf ``FunctionToolset`` like
    ``pydantic_ai_harness.filesystem.FileSystemToolset``).
    """

    def __init__(self, context_dir: Path | str | None, diff_text: str = "") -> None:
        super().__init__(id="review_collector")
        self._context_dir = Path(context_dir) if context_dir else None
        self._diff_text = diff_text
        self.findings: list[RichFinding] = []
        self.uncertainties: list[Uncertainty] = []
        #: Set by ``task_done``; the engine treats loop end without it as a
        #: non-deliberate finish (findings are kept, metrics note it).
        self.finished_deliberately: bool = False
        self.add_function(self._read_context, name="read_context")
        self.add_function(self._record_finding, name="record_finding")
        self.add_function(self._record_uncertainty, name="record_uncertainty")
        self.add_function(self._task_done, name="task_done")

    def _read_context(self, filename: str) -> str:
        """Read one staged deterministic context file (``.reviewforge-context/``).

        ``filename`` must be a bare file name from the staging index —
        no directories, no traversal.
        """
        if self._context_dir is None:
            return "no staged context directory in this run"
        if _invalid_name(filename):
            return "invalid context file name: pass a bare file name from the staging index"
        path = self._context_dir / filename
        if not path.is_file():
            return f"context file {filename!r} is not present in the staging directory"
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            return f"could not read context file {filename!r}: {exc}"
        return _byte_cap_with_pointer(text, _CONTEXT_READ_MAX_BYTES, None)

    def _record_finding(
        self,
        title: str,
        observation: str,
        impact: str,
        recommendation: str,
        severity: Severity,
        confidence: Confidence | None = None,
        file: str | None = None,
        line: int | None = None,
        contextBasis: ContextBasis | None = None,
        regression: bool = False,
        evidence: dict[str, Any] | None = None,
    ) -> str:
        """Record one review finding. Call once per confirmed issue; never batch."""
        payload: dict[str, Any] = {
            "title": title,
            "observation": observation,
            "impact": impact,
            "recommendation": recommendation,
            "severity": severity,
            "confidence": confidence,
            "file": file,
            "line": line,
            "contextBasis": contextBasis,
            "regression": regression,
            "evidence": evidence or {},
        }
        try:
            finding = RichFinding.model_validate(payload)
        except ValidationError as exc:
            # Return the error text as the tool result so the model fixes the
            # arguments and calls again; never raise out of the loop.
            return f"finding NOT recorded — validation error: {_format_validation_error(exc)}"
        self.findings.append(finding)
        return f"recorded (finding #{len(self.findings)})"

    def _record_uncertainty(
        self,
        topic: str,
        reason: str = "",
        confidence: Confidence | None = None,
    ) -> str:
        """Record an area that could not be verified with the available context."""
        try:
            item = Uncertainty.model_validate(
                {"topic": topic, "reason": reason, "confidence": confidence}
            )
        except ValidationError as exc:
            return f"uncertainty NOT recorded — validation error: {_format_validation_error(exc)}"
        self.uncertainties.append(item)
        return f"recorded (uncertainty #{len(self.uncertainties)})"

    def _task_done(self, summary: str = "") -> str:
        """Mark the review as deliberately finished after every changed file had its pass."""
        self.finished_deliberately = True
        return _TASK_DONE_REPLY


def _format_validation_error(exc: ValidationError) -> str:
    """Render a Pydantic ``ValidationError`` as a compact field-level message."""
    parts = []
    for item in exc.errors(include_url=False):
        loc = ".".join(str(part) for part in item.get("loc", ()))
        msg = item.get("msg", "")
        parts.append(f"{loc}: {msg}" if loc else msg)
    return "; ".join(parts) or str(exc)


__all__ = ["ReviewCollectorToolset"]
