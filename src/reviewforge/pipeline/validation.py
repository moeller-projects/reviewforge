"""Validation helpers for stage outputs and the final review doc.

Stage-level validation is intentionally simple: a type check on a few
key fields. Schema-level validation (e.g. pydantic models) lives in
:mod:`reviewforge.pipeline.schemas` and is used when stricter
contracts are needed (final review doc, posted findings).
"""
from __future__ import annotations

from enum import Enum
from typing import Any

from ..config import ConfigError

#: Allowed severity values for a finding.
SEVERITIES: set[str] = {"blocker", "major", "minor", "nit"}
#: Allowed values for ``contextBasis`` on a finding.
BASIS: set[str] = {"diff-only", "surrounding-code-read", "full-module-review"}


class StageLabel(Enum):
    """Identifiers for the review pipeline stages."""

    INTENT_RECONSTRUCTION = "intent reconstruction"
    CONTEXT_PLANNING = "context planning"
    CONTEXT_COLLECT = "context collection"
    CONTEXT_DIGEST = "context digest"
    CANDIDATE_FINDINGS = "candidate findings"
    FINDING_VERIFICATION = "finding verification"
    SEVERITY_CALIBRATION = "severity calibration"


_STAGE_SCHEMA_VALIDATORS = {
    StageLabel.INTENT_RECONSTRUCTION: lambda d: (
        isinstance(d.get("pr_intent"), str)
        and isinstance(d.get("changed_behaviors"), list)
        and isinstance(d.get("risk_areas"), list)
    ),
    StageLabel.CONTEXT_PLANNING: lambda d: (
        isinstance(d.get("files_to_read"), list)
        and isinstance(d.get("searches_to_run"), list)
        and isinstance(d.get("tests_to_inspect"), list)
    ),
    StageLabel.CONTEXT_DIGEST: lambda d: (
        isinstance(d.get("relevant_context"), list)
        and isinstance(d.get("possible_intentional_choices"), list)
        and isinstance(d.get("context_gaps"), list)
    ),
}

_STAGE_SCHEMA_ERRORS = {
    StageLabel.INTENT_RECONSTRUCTION: "[review][ERROR] intent schema invalid",
    StageLabel.CONTEXT_PLANNING: "[review][ERROR] context plan schema invalid",
    StageLabel.CONTEXT_DIGEST: "[review][ERROR] context digest schema invalid",
}

_STAGE_FINDINGS_LABELS = {
    StageLabel.CANDIDATE_FINDINGS,
    StageLabel.FINDING_VERIFICATION,
    StageLabel.SEVERITY_CALIBRATION,
}


def validate_stage(doc: Any, stage: StageLabel | str) -> None:
    """Validate a stage's parsed JSON against its expected shape.

    Stages that produce a ``summary`` + ``findings`` doc (candidate,
    verified, severity) get the same checks as :func:`validate_review_doc`.
    Other stages have their own field-shape validators.
    """
    label = StageLabel(stage) if not isinstance(stage, StageLabel) else stage
    if label in _STAGE_FINDINGS_LABELS:
        validate_review_doc(doc)
        return
    validator = _STAGE_SCHEMA_VALIDATORS.get(label)
    if validator is None:
        return
    if not isinstance(doc, dict) or not validator(doc):
        raise SystemExit(_STAGE_SCHEMA_ERRORS[label])


def validate_review_doc(doc: Any) -> None:
    """Validate the top-level review document: ``summary`` + ``findings`` list."""
    if (
        not isinstance(doc, dict)
        or not isinstance(doc.get("summary"), str)
        or not isinstance(doc.get("findings"), list)
    ):
        raise SystemExit("[review][ERROR] review doc schema invalid")
    for f in doc["findings"]:
        if not isinstance(f, dict):
            raise SystemExit("[review][ERROR] finding is not an object")
        if f.get("severity") not in SEVERITIES:
            raise SystemExit(
                f"[review][ERROR] invalid severity {f.get('severity')!r}; "
                f"expected one of {sorted(SEVERITIES)}"
            )
        if not isinstance(f.get("title"), str) or not f["title"].strip():
            raise SystemExit("[review][ERROR] finding missing non-empty title")
        if not isinstance(f.get("message"), str) or not f["message"].strip():
            raise SystemExit("[review][ERROR] finding missing non-empty message")


def validate_postable_review_doc(doc: Any) -> None:
    """Validate the stricter contract required immediately before posting."""
    validate_review_doc(doc)
    for finding in doc["findings"]:
        suggestion = finding.get("suggestion")
        if not isinstance(suggestion, str) or not suggestion.strip():
            raise SystemExit("[review][ERROR] finding missing non-empty recommendation")
        evidence = finding.get("evidence")
        if not isinstance(evidence, dict):
            raise SystemExit("[review][ERROR] finding missing evidence")
        references = (
            evidence.get("changedLines")
            or evidence.get("changed_lines")
            or evidence.get("contextFilesRead")
            or evidence.get("context_files_read")
            or evidence.get("testsRead")
            or evidence.get("tests_read")
            or evidence.get("workItems")
            or evidence.get("work_items")
            or evidence.get("symbols")
        )
        classification = str(evidence.get("classification") or "").strip()
        rationale = (
            evidence.get("whyNewInThisPr")
            or evidence.get("why_new_in_this_pr")
            or evidence.get("whyNotIntentional")
            or evidence.get("why_not_intentional")
        )
        if not references or (
            not (evidence.get("changedLines") or evidence.get("changed_lines"))
            and not classification
        ):
            raise SystemExit("[review][ERROR] finding evidence is incomplete")
        if not isinstance(rationale, str) or not rationale.strip():
            raise SystemExit("[review][ERROR] finding evidence missing rationale")


__all__ = [
    "BASIS",
    "SEVERITIES",
    "StageLabel",
    "validate_postable_review_doc",
    "validate_review_doc",
    "validate_stage",
]
