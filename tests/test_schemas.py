"""Validator branch coverage for pipeline.schemas."""
from __future__ import annotations

import pytest
from pydantic import ValidationError

from reviewforge.pipeline.schemas import (
    DiscardedFinding,
    Finding,
    GoodPractice,
    ReviewSummary,
    RichEvidence,
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
        assert Uncertainty.model_validate({"topic": "Rollout risk"}).topic == "Rollout risk"


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
