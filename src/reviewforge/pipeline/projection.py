"""Projection layer: transform canonical ``ReviewResult`` into legacy shapes.

Domain models in :mod:`reviewforge.pipeline.schemas` must not know about
presentation formats. This module owns the conversions used by the ADO
posting path and any other consumers that still expect the legacy
``final-findings.json`` shape.
"""
from __future__ import annotations

from typing import Any

from .schemas import ReviewResult, RichFinding, RichSymbol


def _finding_message(finding: RichFinding) -> str:
    """Build a legacy ``message`` from a rich finding."""
    parts = [finding.observation]
    if finding.impact:
        parts.append(finding.impact)
    if finding.recommendation:
        parts.append(finding.recommendation)
    return " ".join(parts)


def _symbol_files(symbols: list[RichSymbol]) -> list[str]:
    """Collect unique file paths from symbol evidence."""
    seen: set[str] = set()
    out: list[str] = []
    for sym in symbols:
        if sym.file and sym.file not in seen:
            seen.add(sym.file)
            out.append(sym.file)
    return out


def _legacy_evidence(evidence: Any) -> dict[str, Any]:
    """Convert rich evidence into the legacy evidence shape."""
    changed_lines = list(evidence.changedLines) if evidence else []
    related_files = list(evidence.relatedFiles) if evidence else []
    tests_read = list(evidence.testsRead) if evidence else []
    work_items = list(evidence.workItems) if evidence else []
    symbol_files = _symbol_files(list(evidence.symbols) if evidence else [])

    context_files_read: list[str] = []
    seen: set[str] = set()
    for path in related_files + tests_read + work_items + symbol_files:
        if path and path not in seen:
            seen.add(path)
            context_files_read.append(path)

    return {
        "changedLines": changed_lines,
        "contextFilesRead": context_files_read,
        "whyNewInThisPr": evidence.whyNewInThisPr if evidence else "",
        "whyNotIntentional": evidence.whyNotIntentional if evidence else "",
        "classification": evidence.classification if evidence else "",
    }


def review_result_to_final_doc(result: ReviewResult) -> dict[str, Any]:
    """Return a legacy ``final-findings.json`` shaped document.

    The output is consumed by :class:`PostToAdoStage` and the ADO comment
    formatter, which expect ``summary`` and ``findings`` fields with the legacy
    ``message``, ``suggestion``, ``confidence``, and ``evidence`` shape.
    """
    findings: list[dict[str, Any]] = []
    for f in result.findings:
        d = f.model_dump(by_alias=True, exclude_none=False)
        d["message"] = _finding_message(f)
        d["confidence"] = f.confidence
        d["suggestion"] = f.recommendation
        d["evidence"] = _legacy_evidence(f.evidence)
        findings.append(d)

    summary = result.review_summary.summary or result.pr_summary.implementation_summary

    return {"summary": summary, "findings": findings}


__all__ = ["review_result_to_final_doc"]
