from __future__ import annotations

from pathlib import Path

import pytest

from reviewforge.artifacts import manager
from reviewforge.pipeline.sarif import review_result_to_sarif
from reviewforge.pipeline.schemas import ReviewResult


def _result(*, severity: str = "major", file: str | None = "src\\app.py", line: int | None = 12) -> ReviewResult:
    finding = {
        "title": "Unsafe input handling!",
        "observation": "Input is used directly.",
        "impact": "This can cause injection.",
        "recommendation": "Validate it before use.",
        "severity": severity,
        "confidence": "high",
        "file": file,
        "line": line,
        "contextBasis": "surrounding-code-read",
        "evidence": {
            "changedLines": [12],
            "relatedFiles": ["src/app.py"],
            "whyNewInThisPr": "The new path accepts user input.",
            "whyNotIntentional": "No validation is present.",
        },
    }
    return ReviewResult.model_validate({"review_summary": {"summary": "Reviewed."}, "findings": [finding]})


def test_review_result_to_sarif_golden_shape():
    output = review_result_to_sarif(_result(), tool_version="1.2.3")
    result = output["runs"][0]["results"][0]
    assert output["version"] == "2.1.0"
    assert output["runs"][0]["tool"]["driver"]["name"] == "ReviewForge"
    assert output["runs"][0]["tool"]["driver"]["version"] == "1.2.3"
    assert output["runs"][0]["tool"]["driver"]["rules"] == [
        {"id": "unsafe-input-handling", "name": "Unsafe input handling!", "shortDescription": {"text": "Unsafe input handling!"}}
    ]
    assert result["message"]["text"] == "Input is used directly. This can cause injection. Validate it before use."
    assert result["locations"][0]["physicalLocation"] == {
        "artifactLocation": {"uri": "src/app.py"}, "region": {"startLine": 12}
    }
    assert result["properties"]["confidence"] == "high"
    assert result["properties"]["contextBasis"] == "surrounding-code-read"
    assert result["properties"]["prbKey"]


def test_severity_mapping():
    expected = {"blocker": "error", "major": "error", "minor": "warning", "nit": "note"}
    for severity, level in expected.items():
        assert review_result_to_sarif(_result(severity=severity), tool_version="x")["runs"][0]["results"][0]["level"] == level


def test_locationless_findings_are_valid_and_rules_are_deduplicated():
    result = _result(file=None, line=None)
    result.findings.append(result.findings[0].model_copy(deep=True))
    output = review_result_to_sarif(result, tool_version="x")
    run = output["runs"][0]
    assert len(run["tool"]["driver"]["rules"]) == 1
    assert len(run["results"]) == 2
    assert all("locations" not in finding for finding in run["results"])


def test_slug_collisions_get_unique_rule_ids():
    result = _result()
    clone = result.findings[0].model_copy(deep=True)
    clone.title = "Unsafe input handling?"
    result.findings.append(clone)
    output = review_result_to_sarif(result, tool_version="x")
    rule_ids = [rule["id"] for rule in output["runs"][0]["tool"]["driver"]["rules"]]
    assert rule_ids == ["unsafe-input-handling", "unsafe-input-handling-2"]


@pytest.mark.parametrize("file", ["../secrets.py", "C:\\repo\\a.py"])
def test_invalid_locations_are_omitted(file: str):
    output = review_result_to_sarif(_result(file=file), tool_version="x")
    assert "locations" not in output["runs"][0]["results"][0]


def test_location_uri_edge_cases():
    from reviewforge.pipeline.sarif import _location_uri, _validate

    assert _location_uri("   ") is None
    assert _location_uri("src/app.py") == "src/app.py"
    with pytest.raises(ValueError, match="missing top-level keys"):
        _validate({"version": "2.1.0"})
    with pytest.raises(ValueError, match="expected version and runs"):
        _validate({"version": "1.0.0", "runs": []})


def test_artifact_manager_exposes_sarif_path(tmp_path: Path):
    cfg = type("Cfg", (), {"review_artifact_dir": str(tmp_path), "review_run_id": None, "review_artifact_root": tmp_path, "pr_id": "1"})()
    artifacts = manager.create(cfg)
    assert artifacts.sarif == tmp_path / "sarif-findings.json"
    assert "sarif-findings.json" in manager.ARTIFACT_NAMES
