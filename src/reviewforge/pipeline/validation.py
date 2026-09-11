"""Validation helpers for stage outputs and the final review doc.

Stage-level validation is intentionally simple: a type check on a few
key fields. Schema-level validation (e.g. pydantic models) lives in
:mod:`reviewforge.pipeline.schemas` and is used when stricter
contracts are needed (final review doc, posted findings).
"""
from __future__ import annotations

from enum import Enum
from typing import Any

from ..exceptions import SchemaValidationError

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
        raise SchemaValidationError(_STAGE_SCHEMA_ERRORS[label], details={"stage": label.value})


def _validate_finding(finding: Any) -> None:
    if not isinstance(finding, dict):
        raise SchemaValidationError("[review][ERROR] finding is not an object")
    if finding.get("severity") not in SEVERITIES:
        raise SchemaValidationError(
            f"[review][ERROR] invalid severity {finding.get('severity')!r}; expected one of {sorted(SEVERITIES)}"
        )
    for field, message in (("title", "finding missing non-empty title"), ("message", "finding missing non-empty message")):
        if not isinstance(finding.get(field), str) or not finding[field].strip():
            raise SchemaValidationError(f"[review][ERROR] {message}")


def validate_review_doc(doc: Any) -> None:
    """Validate the top-level review document: ``summary`` + ``findings`` list."""
    if not isinstance(doc, dict) or not isinstance(doc.get("summary"), str) or not isinstance(doc.get("findings"), list):
        raise SchemaValidationError("[review][ERROR] review doc schema invalid")
    for finding in doc["findings"]:
        _validate_finding(finding)


def _evidence_rationale(evidence: dict[str, Any]) -> str:
    return next(
        (
            evidence.get(key)
            for key in (
                "whyNewInThisPr", "why_new_in_this_pr",
                "whyNotIntentional", "why_not_intentional",
            )
            if evidence.get(key)
        ),
        "",
    )


def _has_evidence_reference(evidence: dict[str, Any]) -> bool:
    return any(
        evidence.get(key)
        for key in (
            "changedLines", "changed_lines", "contextFilesRead", "context_files_read",
            "testsRead", "tests_read", "workItems", "work_items", "symbols", "threads",
        )
    )
def _validate_postable_finding(finding: dict[str, Any]) -> None:
    suggestion = finding.get("suggestion")
    if not isinstance(suggestion, str) or not suggestion.strip():
        raise SchemaValidationError("[review][ERROR] finding missing non-empty recommendation")
    evidence = finding.get("evidence")
    if not isinstance(evidence, dict):
        raise SchemaValidationError("[review][ERROR] finding missing evidence")
    has_lines = evidence.get("changedLines") or evidence.get("changed_lines")
    if not _has_evidence_reference(evidence) or not has_lines and not str(evidence.get("classification") or "").strip():
        raise SchemaValidationError("[review][ERROR] finding evidence is incomplete")
    rationale = _evidence_rationale(evidence)
    if not isinstance(rationale, str) or not rationale.strip():
        raise SchemaValidationError("[review][ERROR] finding evidence missing rationale")


def validate_postable_review_doc(doc: Any) -> None:
    """Validate the stricter contract required immediately before posting."""
    validate_review_doc(doc)
    for finding in doc["findings"]:
        _validate_postable_finding(finding)


__all__ = [
    "BASIS",
    "SEVERITIES",
    "StageLabel",
    "validate_postable_review_doc",
    "validate_review_doc",
    "validate_stage",
]
