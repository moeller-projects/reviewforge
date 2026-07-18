"""Pydantic schemas for structured model outputs.

The reviewer relies on Pi returning strict JSON for several stages. Each
schema here is the contract for one of those outputs. Validating immediately
after parsing gives clear, actionable errors and prevents dangerous
coercions of invalid values (e.g. an unknown severity string silently
becoming ``"nit"``).
"""
from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Literal
import json
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator

# ---------------------------------------------------------------------------
# Enums
# ---------------------------------------------------------------------------

Severity = Literal["nit", "minor", "major", "blocker"]
Confidence = Literal["high", "medium", "low"]
ContextBasis = Literal["diff-only", "surrounding-code-read", "full-module-review"]

# ---------------------------------------------------------------------------
# Base
# ---------------------------------------------------------------------------


class _Base(BaseModel):
    """Common config: tolerate extra keys, ignore them, forbid coercion."""

    model_config = ConfigDict(extra="ignore", str_strip_whitespace=True)


# ---------------------------------------------------------------------------
# Stage 1: intent
# ---------------------------------------------------------------------------


class Intent(_Base):
    """Reconstructed PR intent."""

    pr_intent: str
    changed_behaviors: list[str] = Field(default_factory=list)
    risk_areas: list[str] = Field(default_factory=list)

    @field_validator("pr_intent")
    @classmethod
    def _intent_nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("pr_intent must be a non-empty string")
        return v


# ---------------------------------------------------------------------------
# Stage 2: context plan
# ---------------------------------------------------------------------------


class _FileHint(_Base):
    path: str
    reason: str = ""


class _SearchHint(_Base):
    query: str
    reason: str = ""


class ContextPlan(_Base):
    """What to read / search before reviewing the diff."""

    files_to_read: list[dict[str, Any]] = Field(default_factory=list)
    searches_to_run: list[dict[str, Any]] = Field(default_factory=list)
    tests_to_inspect: list[str] = Field(default_factory=list)


# ---------------------------------------------------------------------------
# Stage 3: context digest
# ---------------------------------------------------------------------------


class ContextDigest(_Base):
    relevant_context: list[Any] = Field(default_factory=list)
    possible_intentional_choices: list[Any] = Field(default_factory=list)
    context_gaps: list[Any] = Field(default_factory=list)


# ---------------------------------------------------------------------------
# Fast review mode
# ---------------------------------------------------------------------------


class ContextSummary(_Base):
    files_read: list[dict[str, Any]] = Field(default_factory=list)
    searches_run: list[dict[str, Any]] = Field(default_factory=list)
    tests_inspected: list[str] = Field(default_factory=list)
    notes: str = ""


class ReviewSummary(_Base):
    summary: str
    notes: str = ""

    @field_validator("summary")
    @classmethod
    def _summary_nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


class VerificationSummary(_Base):
    summary: str
    notes: str = ""

    @field_validator("summary")
    @classmethod
    def _summary_nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


class ReviewStatistics(_Base):
    findings_count: int = Field(default=0, ge=0)
    by_severity: dict[str, int] = Field(default_factory=dict)
    files_read_count: int = Field(default=0, ge=0)
    searches_run_count: int = Field(default=0, ge=0)
    tests_inspected_count: int = Field(default=0, ge=0)

    @field_validator("by_severity")
    @classmethod
    def _validate_by_severity(cls, v: dict[str, int]) -> dict[str, int]:
        allowed = {"blocker", "major", "minor", "nit"}
        for key in v:
            if key not in allowed:
                raise ValueError(f"unexpected severity key: {key}")
        return v


class FastReviewResult(_Base):
    """Top-level rich response returned by the single-call fast review mode."""

    intent: Intent
    context_summary: ContextSummary = Field(default_factory=ContextSummary)
    review_summary: ReviewSummary
    verification_summary: VerificationSummary
    findings: list[Finding] = Field(default_factory=list)
    statistics: ReviewStatistics = Field(default_factory=ReviewStatistics)


# ---------------------------------------------------------------------------
# AC coverage LLM re-check
# ---------------------------------------------------------------------------


class AcCoverageLlmResult(_Base):
    """LLM re-assessment of a single acceptance criterion."""

    covered: bool
    reason: str = ""


# ---------------------------------------------------------------------------
# Findings (candidate, verified, severity, final)
# ---------------------------------------------------------------------------


class Evidence(_Base):
    changedLines: list[int] = Field(default_factory=list)
    contextFilesRead: list[str] = Field(default_factory=list)
    whyNewInThisPr: str = ""
    whyNotIntentional: str = ""


class Finding(_Base):
    """A single review finding."""

    severity: Severity
    title: str
    message: str
    file: str | None = None
    line: int | None = None
    confidence: Confidence | None = None
    contextBasis: ContextBasis | None = None
    suggestion: str | None = None
    evidence: Evidence | None = None

    @field_validator("title", "message")
    @classmethod
    def _nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("must be a non-empty string")
        return v


class ReviewDoc(_Base):
    """The top-level review result: a summary and a list of findings."""

    summary: str
    findings: list[Finding] = Field(default_factory=list)

    @field_validator("summary")
    @classmethod
    def _summary_nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


# ---------------------------------------------------------------------------
# Validation helpers
# ---------------------------------------------------------------------------


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def validate_payload(schema: type[_Base], raw: Any) -> _Base:
    """Validate a parsed-JSON object against ``schema``.

    Raises :class:`pydantic.ValidationError` on failure.
    """
    return schema.model_validate(raw)


def load_and_validate(path: Path, schema: type[_Base]) -> _Base:
    """Read a JSON file from ``path`` and validate against ``schema``."""
    raw = json.loads(path.read_text(encoding="utf-8"))
    return validate_payload(schema, raw)


__all__ = [
    "AcCoverageLlmResult",
    "Confidence",
    "ContextBasis",
    "ContextDigest",
    "ContextPlan",
    "ContextSummary",
    "Evidence",
    "FastReviewResult",
    "Finding",
    "Intent",
    "ReviewDoc",
    "ReviewStatistics",
    "ReviewSummary",
    "Severity",
    "VerificationSummary",
    "load_and_validate",
    "validate_payload",
]
