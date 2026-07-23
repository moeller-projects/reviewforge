"""Pydantic schemas for structured model outputs.

The reviewer relies on Pi returning strict JSON. Each schema here is the
contract for one of those outputs. Validating immediately after parsing gives
clear, actionable errors and prevents dangerous coercions of invalid values
(e.g. an unknown severity string silently becoming ``"nit"``).
"""
from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Literal
import json
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator, model_validator

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
# Legacy multi-stage schemas
# ---------------------------------------------------------------------------


class Intent(_Base):
    """Reconstructed PR intent."""

    pr_intent: str
    changed_behaviors: list[str] = Field(default_factory=list)
    risk_areas: list[str] = Field(default_factory=list)

    @field_validator("pr_intent")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("pr_intent must be a non-empty string")
        return v


class _FileHint(_Base):
    path: str
    reason: str = ""


class _SearchHint(_Base):
    query: str
    reason: str = ""


class ContextPlan(_Base):
    """What to read / search before reviewing the diff."""

    pr_intent: str = ""
    files_to_read: list[_FileHint] = Field(default_factory=list)
    searches_to_run: list[_SearchHint] = Field(default_factory=list)
    tests_to_inspect: list[str] = Field(default_factory=list)


class ContextDigest(_Base):
    relevant_context: list[Any] = Field(default_factory=list)
    possible_intentional_choices: list[Any] = Field(default_factory=list)
    context_gaps: list[Any] = Field(default_factory=list)


class Evidence(_Base):
    changedLines: list[int] = Field(default_factory=list)
    contextFilesRead: list[str] = Field(default_factory=list)
    whyNewInThisPr: str = ""
    whyNotIntentional: str = ""


class Finding(_Base):
    """A single review finding (legacy multi-stage shape)."""

    title: str
    message: str
    severity: Severity
    file: str | None = None
    line: int | None = None
    confidence: str | None = None
    contextBasis: ContextBasis | None = None
    suggestion: str | None = None
    evidence: Evidence = Field(default_factory=Evidence)

    @field_validator("title", "message")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("must be a non-empty string")
        return v


class ReviewDoc(_Base):
    """The top-level review result: a summary and a list of findings."""

    summary: str
    findings: list[Finding] = Field(default_factory=list)

    @field_validator("summary")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


class AcCoverageLlmResult(_Base):
    """LLM re-assessment of a single acceptance criterion."""

    id: int | None = None
    covered: bool
    reason: str = ""


# ---------------------------------------------------------------------------
# Reasoning Engine: canonical rich review result
# ---------------------------------------------------------------------------


class ModelMetadata(_Base):
    """Model/engine metadata recorded for every review run."""

    model: str = ""
    reasoning_engine: str = ""


class TokenUsage(_Base):
    """Token usage reported by the Pi runner."""

    input: int = 0
    output: int = 0
    total: int = 0


class ReviewMetadata(_Base):
    """Run-level metadata for a review."""

    started_at: str = ""
    finished_at: str = ""
    duration_ms: int = 0
    model: ModelMetadata = Field(default_factory=ModelMetadata)
    tokens: TokenUsage = Field(default_factory=TokenUsage)


class PrSummary(_Base):
    """High-level summary of the PR produced by the reasoning engine."""

    intent: str = ""
    implementation_summary: str = ""
    architectural_impact: str = ""
    risk_assessment: str = ""
    positive_observations: list[str] = Field(default_factory=list)


class ReviewSummary(_Base):
    """Overall assessment of the change."""

    summary: str
    notes: str = ""

    @field_validator("summary")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


class VerificationSummary(_Base):
    """How the findings were verified and confidence in that verification."""

    summary: str
    approach: str = ""
    notes: str = ""

    @field_validator("summary")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("summary must be a non-empty string")
        return v


class RichSymbol(_Base):
    """A symbol referenced as evidence for a finding."""

    name: str
    file: str = ""
    line: int | None = None


class RichEvidence(_Base):
    """Evidence supporting a finding."""

    changedLines: list[int] = Field(default_factory=list)
    relatedFiles: list[str] = Field(default_factory=list)
    testsRead: list[str] = Field(default_factory=list)
    workItems: list[str] = Field(default_factory=list)
    symbols: list[RichSymbol] = Field(default_factory=list)
    whyNewInThisPr: str = ""
    whyNotIntentional: str = ""
    classification: str = ""

    @model_validator(mode="after")
    def _meaningful(self) -> "RichEvidence":
        has_reference = bool(
            self.changedLines
            or self.relatedFiles
            or self.testsRead
            or self.workItems
            or self.symbols
        )
        if not has_reference:
            raise ValueError("evidence must contain at least one reference")
        if not self.changedLines and not self.classification.strip():
            raise ValueError(
                "evidence without changed lines requires a classification"
            )
        if not (self.whyNewInThisPr.strip() or self.whyNotIntentional.strip()):
            raise ValueError("evidence must include rationale")
        return self


class RichFinding(_Base):
    """A single rich review finding."""

    title: str
    observation: str
    impact: str
    recommendation: str
    severity: Severity
    confidence: Confidence | None = None
    file: str | None = None
    line: int | None = None
    contextBasis: ContextBasis | None = None
    regression: bool = False
    evidence: RichEvidence = Field(default_factory=RichEvidence)

    @field_validator("title", "observation", "impact", "recommendation")
    @classmethod
    def _nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("must be a non-empty string")
        return v


class DiscardedFinding(_Base):
    """A category of findings that were considered but discarded."""

    reason: str
    category: str = ""
    count: int = Field(default=0, ge=0)

    @field_validator("reason")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("reason must be a non-empty string")
        return v


class GoodPractice(_Base):
    """A positive observation backed by evidence."""

    observation: str
    evidence: str = ""
    files: list[str] = Field(default_factory=list)

    @field_validator("observation")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("observation must be a non-empty string")
        return v


class Uncertainty(_Base):
    """An area where the reviewer is uncertain."""

    topic: str
    reason: str = ""
    confidence: Confidence | None = None

    @field_validator("topic")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("topic must be a non-empty string")
        return v

class ChunkResult(_Base):
    """Partial finding output from one coherent unified-diff chunk."""

    findings: list[RichFinding] = Field(default_factory=list)
    uncertainties: list[Uncertainty] = Field(default_factory=list)




class ReviewMetrics(_Base):
    """Deterministic and model-reported review metrics."""

    changedFilesReviewed: int = Field(default=0, ge=0)
    filesIgnored: int = Field(default=0, ge=0)
    testsRead: int = Field(default=0, ge=0)
    symbolsInspected: int = Field(default=0, ge=0)
    workItemsRead: int = Field(default=0, ge=0)
    confidence: Confidence | None = None
    reviewDepth: str = ""
    piInputTokens: int = Field(default=0, ge=0)
    piOutputTokens: int = Field(default=0, ge=0)
    piTotalTokens: int = Field(default=0, ge=0)
    invocationCount: int = Field(default=0, ge=0)
    repairInvocationCount: int = Field(default=0, ge=0)
    wallClockDurationMs: int = Field(default=0, ge=0)
    reasoningDurationMs: int = Field(default=0, ge=0)
    projectionDurationMs: int = Field(default=0, ge=0)
    validationDurationMs: int = Field(default=0, ge=0)
    estimatedCost: float | None = Field(default=None, ge=0)
    chunkCount: int = Field(default=1, ge=1)
    chunkTokenUsage: list[TokenUsage] = Field(default_factory=list)


class ReviewConfidence(_Base):
    """Review-wide confidence with rationale."""

    level: Confidence | None = None
    reasons: list[str] = Field(default_factory=list)


class ReviewResult(_Base):
    """Top-level structured output from a ReasoningEngine.

    This is the canonical AI response contract. Both the ``single_pi`` and
    ``multi_stage`` engines return this shape. Presentation-layer code
    projects it into legacy formats (e.g. ``final-findings.json``) when needed.
    """

    metadata: ReviewMetadata = Field(default_factory=ReviewMetadata)
    review_summary: ReviewSummary = Field(
        default_factory=lambda: ReviewSummary(summary="No review performed.")
    )
    verification_summary: VerificationSummary = Field(
        default_factory=lambda: VerificationSummary(summary="No verification performed.")
    )
    pr_summary: PrSummary = Field(default_factory=PrSummary)
    findings: list[RichFinding] = Field(default_factory=list)
    discarded_findings: list[DiscardedFinding] = Field(default_factory=list)
    good_practices: list[GoodPractice] = Field(default_factory=list)
    uncertainties: list[Uncertainty] = Field(default_factory=list)

    @model_validator(mode="before")
    @classmethod
    def _require_review_document(cls, data: Any) -> Any:
        if isinstance(data, dict) and data and "review_summary" not in data:
            raise ValueError("review_summary is required in a supplied review document")
        return data
    metrics: ReviewMetrics = Field(default_factory=ReviewMetrics)
    review_confidence: ReviewConfidence = Field(default_factory=ReviewConfidence)


# ---------------------------------------------------------------------------
# Validation helpers
# ---------------------------------------------------------------------------


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def validate_payload(schema: type[_Base], raw: Any) -> _Base:
    """Validate a parsed-JSON object against ``schema``.

    Raises :class:`pydantic.ValidationError` on mismatch.
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
    "DiscardedFinding",
    "Evidence",
    "Finding",
    "GoodPractice",
    "Intent",
    "ModelMetadata",
    "PrSummary",
    "ReviewConfidence",
    "ReviewDoc",
    "ReviewMetadata",
    "ReviewMetrics",
    "ReviewResult",
    "ReviewSummary",
    "RichEvidence",
    "RichFinding",
    "RichSymbol",
    "Severity",
    "TokenUsage",
    "Uncertainty",
    "VerificationSummary",
    "load_and_validate",
    "validate_payload",
]
