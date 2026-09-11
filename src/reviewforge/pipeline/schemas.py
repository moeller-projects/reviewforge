"""Pydantic schemas for structured model outputs.

The reviewer relies on Pi returning strict JSON. Each schema here is the
contract for one of those outputs. Validating immediately after parsing gives
clear, actionable errors and prevents dangerous coercions of invalid values
(e.g. an unknown severity string silently becoming ``"nit"``).
"""
from __future__ import annotations

from typing import Any, Literal
import json
import re
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator, model_validator

# ---------------------------------------------------------------------------
# Enums
# ---------------------------------------------------------------------------

Severity = Literal["nit", "minor", "major", "blocker"]
Confidence = Literal["high", "medium", "low"]
ContextBasis = Literal["diff-only", "surrounding-code-read", "full-module-review"]
WorkType = Literal[
    "feature", "change", "bug", "refactor", "test-only", "docs-config", "mixed"
]
Classification = Literal[
    "work-item", "architectural", "repository-wide", "prior-thread", "other"
]
SuggestedFocus = Literal[
    "security-audit", "deep-logic", "concurrency", "data-integrity"
]
Danger = Literal["high", "critical"]

#: Title prefix identifying a work-item requirement finding. Kept in sync with
#: :data:`reviewforge.ado.posting.WORK_ITEM_TITLE_RE` without importing it.
_WORK_ITEM_TITLE_RE = re.compile(r"^\s*work\s+item\s+#\d+", re.IGNORECASE)

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

class CommentReply(_Base):
    """A model-drafted reply to one existing PR comment thread."""

    thread_id: int
    reply: str


class CommentReplies(_Base):
    """Model output for the reply-to-comments stage."""

    replies: list[CommentReply] = Field(default_factory=list)


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
    work_type: WorkType = "mixed"
    biggest_unknown: str | None = None
    implementation_summary: str = ""
    architectural_impact: str = ""
    risk_assessment: str = ""
    positive_observations: list[str] = Field(default_factory=list)

    @field_validator("positive_observations")
    @classmethod
    def _cap_positive(cls, v: list[str]) -> list[str]:
        if len(v) > 3:
            raise ValueError("positive_observations is capped at 3 entries")
        return v


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
    threads: list[str] = Field(default_factory=list)
    whyNewInThisPr: str = ""
    whyNotIntentional: str = ""
    classification: str = ""

    @field_validator("changedLines")
    @classmethod
    def _positive_lines(cls, v: list[int]) -> list[int]:
        if any(line < 1 for line in v):
            raise ValueError("changedLines must be positive line numbers")
        return v

    @field_validator("classification")
    @classmethod
    def _known_classification(cls, v: str) -> str:
        value = v.strip()
        if value and value not in Classification.__args__:
            raise ValueError(
                f"classification must be one of {Classification.__args__}"
            )
        return value

    @model_validator(mode="after")
    def _meaningful(self) -> "RichEvidence":
        has_reference = bool(
            self.changedLines
            or self.relatedFiles
            or self.testsRead
            or self.workItems
            or self.symbols
            or self.threads
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
    line: int | None = Field(default=None, ge=1)
    contextBasis: ContextBasis | None = None
    regression: bool = False
    evidence: RichEvidence = Field(default_factory=RichEvidence)

    @field_validator("title", "observation", "impact", "recommendation")
    @classmethod
    def _nonempty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("must be a non-empty string")
        return v

    @field_validator("file")
    @classmethod
    def _repo_relative(cls, v: str | None) -> str | None:
        if v is None:
            return None
        raw = str(v)
        if raw.startswith(("/", "\\")):
            raise ValueError("file must be repo-relative with no leading slash")
        normalized = raw.replace("\\", "/")
        if not normalized:
            raise ValueError("file must be a non-empty repo-relative path")
        if re.match(r"^[A-Za-z]:", normalized) or normalized.startswith("//"):
            raise ValueError("file must be repo-relative with no leading slash")
        if any(part == ".." for part in normalized.split("/")):
            raise ValueError("file must not contain parent-directory traversal")
        return normalized

    @model_validator(mode="after")
    def _constraints(self) -> "RichFinding":
        _validate_regression_constraint(self)
        _validate_work_item_constraint(self)
        _validate_prior_thread_constraint(self)
        return self

def _validate_regression_constraint(finding: Any) -> None:
    if finding.regression and not finding.evidence.changedLines:
        raise ValueError("regression findings must cite changed lines")


def _is_work_item_finding(finding: Any) -> bool:
    return bool(
        _WORK_ITEM_TITLE_RE.match(finding.title or "")
        or finding.evidence.classification == "work-item"
    )


def _validate_work_item_constraint(finding: Any) -> None:
    if not _is_work_item_finding(finding):
        return
    if finding.file is not None or finding.line is not None:
        raise ValueError("work-item findings must not anchor to a file or line")
    if finding.severity not in ("major", "blocker"):
        raise ValueError("work-item findings require severity major or blocker")


def _validate_prior_thread_constraint(finding: Any) -> None:
    if finding.evidence.classification == "prior-thread" and not finding.evidence.threads:
        raise ValueError("prior-thread findings must cite the thread id")



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

    @model_validator(mode="after")
    def _resolvable(self) -> "Uncertainty":
        if self.topic.startswith("cross-chunk:") and self.confidence != "low":
            raise ValueError("cross-chunk uncertainties must set confidence to low")
        return self


class CoverageGap(_Base):
    """A changed behavior without test coverage."""

    behavior: str
    suggested_test: str
    file: str

    @field_validator("behavior", "suggested_test", "file")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("must be a non-empty string")
        return v

    @field_validator("file")
    @classmethod
    def _repo_relative(cls, v: str) -> str:
        normalized = v.replace("\\", "/")
        if normalized.startswith("/") or re.match(r"^[A-Za-z]:/", normalized):
            raise ValueError("file must be a repo-relative path")
        if not normalized or any(part == ".." for part in normalized.split("/")):
            raise ValueError("file must be a repo-relative path")
        return normalized


class EscalationHint(_Base):
    """A request for a deeper or specialised review pass."""

    files: list[str] = Field(default_factory=list, min_length=1)
    reason: str
    suggested_focus: SuggestedFocus
    danger: Danger

    @field_validator("reason")
    @classmethod
    def _non_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("reason must be a non-empty string")
        return v


class ChunkResult(_Base):
    """Partial finding output from one coherent unified-diff chunk."""

    findings: list[RichFinding] = Field(default_factory=list)
    test_gaps: list[CoverageGap] = Field(default_factory=list)
    uncertainties: list[Uncertainty] = Field(default_factory=list)
    escalation_hints: list[EscalationHint] = Field(default_factory=list)
    discarded_findings: list[DiscardedFinding] = Field(default_factory=list)


class ChunkSynthesis(_Base):
    """Whole-PR summaries synthesized from prior per-chunk analyses."""

    review_summary: ReviewSummary
    verification_summary: VerificationSummary = Field(
        default_factory=lambda: VerificationSummary(
            summary="Reviewed each deterministic unified-diff chunk.",
            approach="chunked diff review",
        )
    )
    pr_summary: PrSummary = Field(default_factory=PrSummary)
    good_practices: list[GoodPractice] = Field(default_factory=list)

    @model_validator(mode="before")
    @classmethod
    def _require_framing(cls, data: Any) -> Any:
        if isinstance(data, dict) and data:
            pr_summary = data.get("pr_summary")
            if not isinstance(pr_summary, dict) or "work_type" not in pr_summary:
                raise ValueError("chunk synthesis pr_summary.work_type is required")
            if not str(pr_summary.get("intent", "")).strip():
                raise ValueError("chunk synthesis pr_summary.intent is required")
        return data




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
    test_gaps: list[CoverageGap] = Field(default_factory=list)
    escalation_hints: list[EscalationHint] = Field(default_factory=list)
    metrics: ReviewMetrics = Field(default_factory=ReviewMetrics)
    review_confidence: ReviewConfidence = Field(default_factory=ReviewConfidence)

    @model_validator(mode="before")
    @classmethod
    def _require_review_document(cls, data: Any) -> Any:
        if isinstance(data, dict) and data and "review_summary" not in data:
            raise ValueError("review_summary is required in a supplied review document")
        return data

    @field_validator("test_gaps")
    @classmethod
    def _cap_test_gaps(cls, v: list[CoverageGap]) -> list[CoverageGap]:
        if len(v) > 5:
            raise ValueError("test_gaps is capped at 5 entries")
        return v

    @field_validator("escalation_hints")
    @classmethod
    def _cap_hints(cls, v: list[EscalationHint]) -> list[EscalationHint]:
        if len(v) > 3:
            raise ValueError("escalation_hints is capped at 3 entries")
        return v

    @field_validator("good_practices")
    @classmethod
    def _cap_good(cls, v: list[GoodPractice]) -> list[GoodPractice]:
        if len(v) > 3:
            raise ValueError("good_practices is capped at 3 entries")
        return v


# ---------------------------------------------------------------------------
# Validation helpers
# ---------------------------------------------------------------------------


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
    "ChunkSynthesis",
    "ChunkResult",
    "Classification",
    "CommentReplies",
    "CommentReply",
    "Confidence",
    "ContextBasis",
    "ContextDigest",
    "ContextPlan",
    "Danger",
    "DiscardedFinding",
    "EscalationHint",
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
    "SuggestedFocus",
    "CoverageGap",
    "TokenUsage",
    "Uncertainty",
    "VerificationSummary",
    "WorkType",
    "load_and_validate",
    "validate_payload",
]
