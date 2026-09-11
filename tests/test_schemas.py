"""Validator branch coverage for pipeline.schemas."""
from __future__ import annotations

import pytest
from pydantic import ValidationError

from reviewforge.pipeline.schemas import (
    DiscardedFinding,
    EscalationHint,
    Finding,
    GoodPractice,
    PrSummary,
    ReviewResult,
    ReviewSummary,
    RichEvidence,
    RichFinding,
    Uncertainty,
    VerificationSummary,
)


def _finding(**overrides):
    payload = {
        "title": "Bug",
        "message": "Something is wrong.",
        "severity": "major",
    }
    payload.update(overrides)
    return payload


class TestNonEmptyValidators:
    def test_finding_title_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            Finding.model_validate(_finding(title="   "))
        assert Finding.model_validate(_finding()).title == "Bug"

    def test_review_summary_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            ReviewSummary.model_validate({"summary": ""})
        assert ReviewSummary.model_validate({"summary": "ok"}).summary == "ok"

    def test_verification_summary_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            VerificationSummary.model_validate({"summary": " "})
        assert VerificationSummary.model_validate({"summary": "ok"}).summary == "ok"

    def test_discarded_finding_reason_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            DiscardedFinding.model_validate(
                {"title": "Bug", "observation": "o", "impact": "i", "recommendation": "r", "reason": ""}
            )

    def test_good_practice_observation_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            GoodPractice.model_validate({"observation": ""})
        assert GoodPractice.model_validate({"observation": "Clean tests."}).observation == "Clean tests."

    def test_uncertainty_topic_must_be_non_empty(self):
        with pytest.raises(ValidationError, match="non-empty"):
            Uncertainty.model_validate({"topic": ""})
        assert Uncertainty.model_validate({"topic": "Rollout risk", "reason": ""}).reason == ""


class TestRichEvidence:
    def test_requires_at_least_one_reference(self):
        with pytest.raises(ValidationError, match="at least one reference"):
            RichEvidence.model_validate({"whyNewInThisPr": "new"})

    def test_non_line_references_require_classification(self):
        with pytest.raises(ValidationError, match="requires a classification"):
            RichEvidence.model_validate({"relatedFiles": ["a.py"], "whyNewInThisPr": "new"})

    def test_requires_rationale(self):
        with pytest.raises(ValidationError, match="must include rationale"):
            RichEvidence.model_validate({"changedLines": [1]})

    def test_valid_evidence_passes(self):
        evidence = RichEvidence.model_validate({"changedLines": [1], "whyNewInThisPr": "new in PR"})
        assert evidence.changedLines == [1]


class TestRichFinding:
    @staticmethod
    def _payload():
        return {
            "title": "Bug",
            "observation": "Something is wrong.",
            "impact": "Users are affected.",
            "recommendation": "Fix it.",
            "severity": "major",
        }

    def test_evidence_is_required(self):
        with pytest.raises(ValidationError):
            RichFinding.model_validate(self._payload())

    def test_evidence_cannot_be_null(self):
        with pytest.raises(ValidationError):
            RichFinding.model_validate(self._payload() | {"evidence": None})

    @pytest.mark.parametrize("path", ["/outside.py", r"\\unc\path"])
    def test_file_path_must_be_repo_relative(self, path: str):
        with pytest.raises(ValidationError, match="repo-relative"):
            RichFinding.model_validate(
                self._payload()
                | {
                    "file": path,
                    "evidence": {"changedLines": [1], "whyNewInThisPr": "new in PR"},
                }
            )


class TestWorkItemAndRegression:
    @staticmethod
    def _finding(**overrides):
        payload = {
            "title": "Bug",
            "observation": "o",
            "impact": "i",
            "recommendation": "r",
            "severity": "major",
            "evidence": {"changedLines": [1], "whyNewInThisPr": "new"},
        }
        payload.update(overrides)
        return payload

    def test_regression_requires_changed_lines(self):
        with pytest.raises(ValidationError, match="regression"):
            RichFinding.model_validate(
                self._finding(
                    regression=True,
                    evidence={"relatedFiles": ["a.py"], "whyNewInThisPr": "new", "classification": "other"},
                )
            )

    def test_work_item_finding_rejects_anchor(self):
        with pytest.raises(ValidationError, match="work-item"):
            RichFinding.model_validate(
                self._finding(title="Work item #12 requirement not addressed: x", file="a.py", line=1)
            )

    def test_prior_thread_requires_thread_id(self):
        with pytest.raises(ValidationError, match="prior-thread"):
            RichFinding.model_validate(
                self._finding(
                    evidence={
                        "changedLines": [1],
                        "whyNewInThisPr": "new",
                        "classification": "prior-thread",
                    }
                )
            )


class TestContractCaps:
    def _gap(self, index: int) -> dict:
        return {"behavior": f"b{index}", "suggested_test": f"t{index}", "file": "a.py"}

    def test_test_gaps_capped_at_five(self):
        with pytest.raises(ValidationError, match="test_gaps"):
            ReviewResult.model_validate(
                {
                    "review_summary": {"summary": "s"},
                    "pr_summary": {"work_type": "change"},
                    "test_gaps": [self._gap(i) for i in range(6)],
                }
            )

    def test_escalation_hints_capped_at_three(self):
        hints = [
            {"files": ["a.py"], "reason": f"r{i}", "suggested_focus": "security-audit", "danger": "high"}
            for i in range(4)
        ]
        with pytest.raises(ValidationError, match="escalation_hints"):
            ReviewResult.model_validate(
                {"review_summary": {"summary": "s"}, "pr_summary": {"work_type": "change"}, "escalation_hints": hints}
            )

    def test_work_type_is_validated(self):
        with pytest.raises(ValidationError):
            PrSummary.model_validate({"work_type": "nonsense"})

    def test_legacy_document_without_pr_summary_uses_defaults(self):
        result = ReviewResult.model_validate({"review_summary": {"summary": "s"}})
        assert result.pr_summary.work_type == "mixed"

    def test_legacy_pr_summary_without_work_type_uses_default(self):
        result = ReviewResult.model_validate(
            {"review_summary": {"summary": "s"}, "pr_summary": {"intent": "Fix bug"}}
        )
        assert result.pr_summary.work_type == "mixed"

    def test_escalation_hint_enums(self):
        with pytest.raises(ValidationError):
            EscalationHint.model_validate(
                {"files": ["a.py"], "reason": "r", "suggested_focus": "wrong", "danger": "high"}
            )
