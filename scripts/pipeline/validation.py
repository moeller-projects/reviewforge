from __future__ import annotations
from enum import Enum
from typing import Any

SEVERITIES = {"blocker", "major", "minor", "nit"}
BASIS = {"diff-only", "surrounding-code-read", "full-module-review"}

class StageLabel(Enum):
    INTENT_RECONSTRUCTION = "intent reconstruction"
    CONTEXT_PLANNING = "context planning"
    CONTEXT_DIGEST = "context digest"
    CANDIDATE_FINDINGS = "candidate findings"
    FINDING_VERIFICATION = "finding verification"
    SEVERITY_CALIBRATION = "severity calibration"

_STAGE_SCHEMA_VALIDATORS = {
    StageLabel.INTENT_RECONSTRUCTION: lambda d: isinstance(d.get('pr_intent'), str) and isinstance(d.get('changed_behaviors'), list) and isinstance(d.get('risk_areas'), list),
    StageLabel.CONTEXT_PLANNING: lambda d: isinstance(d.get('files_to_read'), list) and isinstance(d.get('searches_to_run'), list) and isinstance(d.get('tests_to_inspect'), list),
    StageLabel.CONTEXT_DIGEST: lambda d: isinstance(d.get('relevant_context'), list) and isinstance(d.get('possible_intentional_choices'), list) and isinstance(d.get('context_gaps'), list),
}

_STAGE_SCHEMA_ERRORS = {
    StageLabel.INTENT_RECONSTRUCTION: '[review][ERROR] intent schema invalid',
    StageLabel.CONTEXT_PLANNING: '[review][ERROR] context plan schema invalid',
    StageLabel.CONTEXT_DIGEST: '[review][ERROR] context digest schema invalid',
}

_STAGE_FINDINGS_LABELS = {
    StageLabel.CANDIDATE_FINDINGS,
    StageLabel.FINDING_VERIFICATION,
    StageLabel.SEVERITY_CALIBRATION,
}


def validate_review_doc(doc: Any) -> None:
    if not isinstance(doc, dict) or not isinstance(doc.get('summary'), str) or not isinstance(doc.get('findings'), list):
        raise SystemExit('[review][ERROR] Pi output did not match expected JSON contract')
    for item in doc['findings']:
        if not isinstance(item, dict) or item.get('severity') not in SEVERITIES or not isinstance(item.get('title'), str) or not isinstance(item.get('message'), str):
            raise SystemExit('[review][ERROR] invalid finding schema')
        if item.get('context_basis') is not None and item.get('context_basis') not in BASIS:
            raise SystemExit('[review][ERROR] invalid context_basis')
        if item.get('confidence') is not None and item.get('confidence') not in {'high','medium','low'}:
            raise SystemExit('[review][ERROR] invalid confidence')


def _resolve_stage_label(label: str) -> StageLabel:
    for stage in StageLabel:
        if stage.value == label:
            return stage
    raise SystemExit(f'[review][ERROR] unknown validation stage label: {label}')


def validate_stage(doc: Any, label: str) -> None:
    if not isinstance(doc, dict):
        raise SystemExit(f'[review][ERROR] {label} output is not a JSON object')
    stage = _resolve_stage_label(label)
    if stage in _STAGE_SCHEMA_VALIDATORS:
        if not _STAGE_SCHEMA_VALIDATORS[stage](doc):
            raise SystemExit(_STAGE_SCHEMA_ERRORS[stage])
    if stage in _STAGE_FINDINGS_LABELS:
        validate_review_doc(doc)
