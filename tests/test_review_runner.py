"""Unit coverage for the Python review runner modules."""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
sys.path.insert(0, str(SRC))

from auto_pr_reviewer.ado import client as ado_client  # noqa: E402
from auto_pr_reviewer.artifacts import builder as artifact_builder  # noqa: E402
from auto_pr_reviewer.pipeline.validation import validate_review_doc  # noqa: E402


def test_parse_dev_azure_pr_url():
    assert ado_client.parse_pr_url(
        "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"
    ) == ("contoso", "Payments", "payments-api", "1423")


def test_parse_visualstudio_pr_url():
    assert ado_client.parse_pr_url(
        "https://contoso.visualstudio.com/Payments/_git/payments-api/pullrequest/1423"
    ) == ("contoso", "Payments", "payments-api", "1423")


def test_parse_pr_url_rejects_unknown_format():
    with pytest.raises(SystemExit):
        ado_client.parse_pr_url("https://example.com/pr/1")


def test_build_changed_files_classifies_language_and_tests():
    entries = artifact_builder.changed_files(["src/app.py", "tests/app.test.ts", "README.md"])
    assert entries == [
        {"file": "src/app.py", "language": "Python", "isTest": False},
        {"file": "tests/app.test.ts", "language": "TypeScript", "isTest": True},
        {"file": "README.md", "language": "Markdown", "isTest": False},
    ]


def test_review_doc_validation_accepts_minimal_document():
    validate_review_doc(
        {"summary": "ok", "findings": [{"severity": "major", "title": "T", "message": "M"}]}
    )
