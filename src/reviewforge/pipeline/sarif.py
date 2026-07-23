"""SARIF projection for canonical review results."""
from __future__ import annotations

import re
from typing import Any

from ..ado.posting import dedupe_key
from .schemas import ReviewResult, RichFinding

_REPO_URL = "https://dev.azure.com/aveato/auto-pr-reviewer/_git/auto-pr-reviewer"
_LEVELS = {"blocker": "error", "major": "error", "minor": "warning", "nit": "note"}


def _rule_id(title: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
    return slug or "finding"


def _message(finding: RichFinding) -> str:
    return " ".join((finding.observation, finding.impact, finding.recommendation))


def _evidence_summary(finding: RichFinding) -> dict[str, Any]:
    evidence = finding.evidence
    return {
        "changedLines": list(evidence.changedLines),
        "relatedFiles": list(evidence.relatedFiles),
        "testsRead": list(evidence.testsRead),
        "workItems": list(evidence.workItems),
        "whyNewInThisPr": evidence.whyNewInThisPr,
        "whyNotIntentional": evidence.whyNotIntentional,
        "classification": evidence.classification,
    }


def _validate(log: dict[str, Any]) -> None:
    if not all(key in log for key in ("version", "runs")):
        raise ValueError("invalid SARIF log: missing top-level keys")
    if log["version"] != "2.1.0" or not isinstance(log["runs"], list):
        raise ValueError("invalid SARIF log: expected version and runs")


def review_result_to_sarif(result: ReviewResult, *, tool_version: str) -> dict[str, Any]:
    """Render a canonical result as a minimal SARIF 2.1.0 log."""
    rules: list[dict[str, Any]] = []
    rule_ids: dict[str, str] = {}
    sarif_results: list[dict[str, Any]] = []
    for finding in result.findings:
        if finding.title not in rule_ids:
            rule_ids[finding.title] = _rule_id(finding.title)
            rules.append({"id": rule_ids[finding.title], "name": finding.title, "shortDescription": {"text": finding.title}})
        rule_id = rule_ids[finding.title]
        item: dict[str, Any] = {
            "ruleId": rule_id,
            "level": _LEVELS[str(finding.severity)],
            "message": {"text": _message(finding)},
            "properties": {
                "confidence": finding.confidence,
                "contextBasis": finding.contextBasis,
                "evidence": _evidence_summary(finding),
                "prbKey": dedupe_key(finding.model_dump(by_alias=True)),
            },
        }
        if finding.file and finding.line is not None:
            item["locations"] = [{"physicalLocation": {"artifactLocation": {"uri": finding.file.replace("\\", "/").lstrip("/")}, "region": {"startLine": finding.line}}}]
        sarif_results.append(item)

    metadata = result.metadata
    model = metadata.model
    tokens = metadata.tokens
    metrics = result.metrics
    run = {
        "tool": {
            "driver": {
                "name": "ReviewForge",
                "version": tool_version,
                "informationUri": _REPO_URL,
                "rules": rules,
            }
        },
        "results": sarif_results,
        "properties": {
            "prId": result.metadata.model_dump().get("pr_id", ""),
            "model": model.model,
            "reasoningEngine": model.reasoning_engine,
            "inputTokens": tokens.input or metrics.piInputTokens,
            "outputTokens": tokens.output or metrics.piOutputTokens,
            "totalTokens": tokens.total or metrics.piTotalTokens,
        },
    }
    log = {"version": "2.1.0", "$schema": "https://json.schemastore.org/sarif-2.1.0.json", "runs": [run]}
    _validate(log)
    return log


__all__ = ["review_result_to_sarif"]
