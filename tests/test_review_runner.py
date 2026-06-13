"""Unit coverage for the Python review runner modules."""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))


def load(name: str, path: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module

ado_client = load("ado_client", "scripts/infrastructure/ado/client.py")
artifact_builder = load("artifact_builder", "scripts/infrastructure/artifacts/builder.py")
validation = load("validation", "scripts/pipeline/validation.py")


def test_parse_dev_azure_pr_url():
    assert ado_client.parse_pr_url(
        "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"
    ) == ("contoso", "Payments", "payments-api", "1423")


def test_parse_visualstudio_pr_url():
    assert ado_client.parse_pr_url(
        "https://contoso.visualstudio.com/Payments/_git/payments-api/pullrequest/1423"
    ) == ("contoso", "Payments", "payments-api", "1423")


def test_build_changed_files_classifies_language_and_tests():
    entries = artifact_builder.changed_files(["src/app.py", "tests/app.test.ts", "README.md"])
    assert entries == [
        {"file": "src/app.py", "language": "Python", "isTest": False},
        {"file": "tests/app.test.ts", "language": "TypeScript", "isTest": True},
        {"file": "README.md", "language": "Markdown", "isTest": False},
    ]


def test_review_doc_validation_accepts_minimal_document():
    validation.validate_review_doc(
        {"summary": "ok", "findings": [{"severity": "major", "title": "T", "message": "M"}]}
    )
