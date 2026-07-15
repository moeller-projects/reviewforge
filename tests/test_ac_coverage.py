"""Tests for the acceptance-criteria coverage check.

Covers the pure-logic helpers in ``ado.ac_coverage`` and the end-to-end
``AcceptanceCriteriaCoverageStage``.
"""
from __future__ import annotations

from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

from reviewforge.ado import ac_coverage
from reviewforge.ado.ac_coverage import (
    AcCoverageResult,
    check_ac_coverage,
    extract_identifiers,
    iter_acceptance_criteria,
    strip_html,
    uncovered_findings,
)
from reviewforge.artifacts import builder, manager
from reviewforge.pipeline.stage import StageContext
from reviewforge.pipeline.stages import AcceptanceCriteriaCoverageStage


# ---------------------------------------------------------------------------
# strip_html
# ---------------------------------------------------------------------------


class TestStripHtml:
    def test_empty_input(self):
        assert strip_html("") == ""
        assert strip_html(None) == ""

    def test_plain_text_unchanged(self):
        assert strip_html("Charge a card") == "Charge a card"

    def test_strips_simple_tags(self):
        assert strip_html("<p>Charge a card</p>") == " Charge a card "

    def test_decodes_entities(self):
        assert strip_html("Foo &amp; bar &lt;baz&gt;") == "Foo & bar <baz>"

    def test_handles_multiline_html(self):
        text = "<div><p>Line 1</p><p>Line 2</p></div>"
        out = strip_html(text)
        assert "Line 1" in out
        assert "Line 2" in out
        assert "<" not in out


# ---------------------------------------------------------------------------
# extract_identifiers
# ---------------------------------------------------------------------------


class TestExtractIdentifiers:
    def test_extracts_file_path(self):
        idents = extract_identifiers("Update src/payments/charge.ts to handle refunds")
        assert "src/payments/charge.ts" in idents

    def test_extracts_pascal_case(self):
        idents = extract_identifiers("Add a new PaymentProcessor class")
        assert "PaymentProcessor" in idents

    def test_extracts_snake_case(self):
        idents = extract_identifiers("Add a new charge_refund handler")
        assert "charge_refund" in idents

    def test_extracts_def_keyword_references(self):
        idents = extract_identifiers("Implement def calculate_total")
        assert "calculate_total" in idents

    def test_extracts_class_keyword_references(self):
        idents = extract_identifiers("Add class RefundService")
        assert "RefundService" in idents

    def test_extracts_backtick_quoted(self):
        idents = extract_identifiers("The flag `cfg.dry_run` must be honored")
        assert "cfg.dry_run" in idents

    def test_extracts_quoted_strings(self):
        idents = extract_identifiers('Support the "refund_reason" field')
        assert "refund_reason" in idents

    def test_strips_html_before_extraction(self):
        idents = extract_identifiers("<p>Update <code>src/foo.py</code></p>")
        assert "src/foo.py" in idents

    def test_drops_url_paths(self):
        idents = extract_identifiers("See https://example.com/spec.html for details")
        assert not any(i.startswith("http") for i in idents)

    def test_drops_version_strings(self):
        idents = extract_identifiers("Tested against v1.2.3 of the SDK")
        assert "1.2.3" not in idents

    def test_empty_input_returns_empty(self):
        assert extract_identifiers("") == set()
        assert extract_identifiers(None) == set()

    def test_no_identifiers_in_prose(self):
        # Pure English sentence with no paths / class names / etc.
        assert extract_identifiers("User can click the button") == set()


# ---------------------------------------------------------------------------
# iter_acceptance_criteria
# ---------------------------------------------------------------------------


def _wi(wi_id, title="X", ac="Some criterion"):
    return {
        "id": wi_id,
        "type": "User Story",
        "title": title,
        "description": "...",
        "acceptanceCriteria": ac,
    }


class TestIterAcceptanceCriteria:
    def test_flattens_one_per_wi(self):
        items = [_wi(1, ac="AC 1"), _wi(2, ac="AC 2")]
        out = iter_acceptance_criteria(items)
        assert [o["work_item_id"] for o in out] == [1, 2]
        assert [o["ac_text"] for o in out] == ["AC 1", "AC 2"]

    def test_skips_missing_ac(self):
        items = [_wi(1, ac=None), _wi(2, ac=""), _wi(3, ac="AC 3")]
        out = iter_acceptance_criteria(items)
        assert [o["work_item_id"] for o in out] == [3]

    def test_skips_placeholder_ac(self):
        # ADO returns ``"(none)"`` for items with no AC set.
        items = [_wi(1, ac="(none)"), _wi(2, ac="AC 2"), _wi(3, ac="N/A")]
        out = iter_acceptance_criteria(items)
        assert [o["work_item_id"] for o in out] == [2]

    def test_handles_empty_list(self):
        assert iter_acceptance_criteria([]) == []
        assert iter_acceptance_criteria(None) == []


# ---------------------------------------------------------------------------
# check_ac_coverage
# ---------------------------------------------------------------------------


class TestCheckAcCoverage:
    def test_covered_by_changed_file(self):
        items = [_wi(1, ac="Update src/payments/charge.ts to handle refunds")]
        diff = "+ new line\n"
        results = check_ac_coverage(items, diff, ["src/payments/charge.ts"])
        assert results[0].is_covered
        assert "src/payments/charge.ts" in results[0].matched

    def test_covered_by_diff_text(self):
        items = [_wi(1, ac="Add PaymentProcessor class for refunds")]
        diff = "+ class PaymentProcessor:\n+    pass\n"
        results = check_ac_coverage(items, diff, ["some_other.py"])
        assert results[0].is_covered
        assert "PaymentProcessor" in results[0].matched

    def test_uncovered_when_no_match(self):
        items = [_wi(1, ac="Update src/payments/charge.ts to handle refunds")]
        diff = "+ x = 1\n"
        results = check_ac_coverage(items, diff, ["src/other.py"])
        assert not results[0].is_covered
        assert results[0].reason == "no_identifier_in_diff"

    def test_uncovered_when_no_identifiers(self):
        items = [_wi(1, ac="User can click the button")]
        diff = "+ x = 1\n"
        results = check_ac_coverage(items, diff, ["any.py"])
        assert not results[0].is_covered
        assert results[0].reason == "no_identifiers_extracted"
        assert results[0].identifiers == ()

    def test_multiple_work_items_mixed(self):
        items = [
            _wi(1, ac="Update src/foo.py"),
            _wi(2, ac="Update src/missing.py"),
        ]
        results = check_ac_coverage(items, "+ x\n", ["src/foo.py"])
        assert results[0].is_covered
        assert not results[1].is_covered

    def test_case_insensitive_match(self):
        items = [_wi(1, ac="Update SRC/Payments/CHARGE.ts")]
        results = check_ac_coverage(items, "", ["src/payments/charge.ts"])
        assert results[0].is_covered

    def test_short_text_truncates(self):
        long_ac = "word " * 100
        items = [_wi(1, ac=long_ac)]
        results = check_ac_coverage(items, "", [])
        assert results[0].is_covered is False  # no identifiers
        assert len(results[0].short_text) <= 80

    def test_empty_inputs(self):
        # Empty list of work items, empty diff, empty changed files.
        assert check_ac_coverage([], "", []) == []
        assert check_ac_coverage(None, "", []) == []


# ---------------------------------------------------------------------------
# uncovered_findings
# ---------------------------------------------------------------------------


class TestUncoveredFindings:
    def test_returns_only_uncovered(self):
        results = [
            AcCoverageResult(work_item_id=1, ac_text="AC 1", is_covered=True),
            AcCoverageResult(work_item_id=2, ac_text="AC 2", is_covered=False, reason="x"),
            AcCoverageResult(work_item_id=3, ac_text="AC 3", is_covered=True),
        ]
        out = uncovered_findings(results)
        assert len(out) == 1
        assert out[0]["file"] is None
        assert out[0]["line"] is None
        assert out[0]["severity"] == "major"
        assert "Work item #2" in out[0]["title"]
        assert "AC 2" in out[0]["title"]

    def test_finding_title_prefix_for_general_thread(self):
        # The ``Work item #N`` prefix triggers the existing
        # ``is_work_item_finding`` rule in posting.py → forces general
        # comment regardless of any model-guessed file/line.
        results = [
            AcCoverageResult(work_item_id=42, ac_text="x", is_covered=False, reason="r"),
        ]
        out = uncovered_findings(results)
        assert out[0]["title"].startswith("Work item #42 acceptance criterion not covered:")

    def test_message_includes_ac_text(self):
        results = [
            AcCoverageResult(
                work_item_id=1,
                ac_text="Update charge flow with new validation",
                identifiers=("charge",),
                is_covered=False,
                reason="no_identifier_in_diff",
            ),
        ]
        out = uncovered_findings(results)
        assert "Update charge flow with new validation" in out[0]["message"]
        assert "Identifiers extracted" in out[0]["message"]

    def test_message_handles_empty_ac_text(self):
        results = [
            AcCoverageResult(work_item_id=1, ac_text="", is_covered=False, reason="r"),
        ]
        out = uncovered_findings(results)
        assert "(empty)" in out[0]["message"]


# ---------------------------------------------------------------------------
# AcceptanceCriteriaCoverageStage (end-to-end)
# ---------------------------------------------------------------------------


def _stage_ctx(tmp_path, *, dry_run=False, work_items=None, diff_text=""):
    """Build a StageContext with a minimal artifact tree."""
    from reviewforge.config import Config

    # Minimal prompt files to satisfy validate_files().
    prompt_files = {}
    for n in ("review", "intent", "plan", "digest", "verify", "severity", "standards"):
        p = tmp_path / f"{n}.md"
        p.write_text(f"{n}", encoding="utf-8")
        prompt_files[n] = p

    cfg = Config(
        ado_org="o", ado_project="P", ado_repo_id="r", pr_id="42", ado_token="t",
        source_branch="feature", target_branch="main",
        workspace=tmp_path, clone_root=tmp_path, review_language="English",
        review_prompt_path=prompt_files["review"], intent_prompt_path=prompt_files["intent"],
        context_plan_prompt_path=prompt_files["plan"],
        context_digest_prompt_path=prompt_files["digest"],
        verify_prompt_path=prompt_files["verify"],
        severity_prompt_path=prompt_files["severity"],
        standards_path=prompt_files["standards"],
        pi_model="m", max_diff_bytes=200000, chunk_trigger_diff_bytes=200000,
        disable_chunk_review=False, pi_timeout_secs=5, dry_run=dry_run,
        include_work_items=True, include_existing_comments=True,
        verify_findings=True, force_review=False, review_target_branches="",
        review_artifact_dir=None, review_artifact_root=tmp_path / "artifacts",
        review_run_id="r1",
    )
    artifacts = manager.create(cfg)
    if work_items is not None:
        artifacts.work_items.write_text(__import__("json").dumps(work_items), encoding="utf-8")
    if diff_text:
        artifacts.diff.write_text(diff_text, encoding="utf-8")
    builder.write_json(
        artifacts.changed_files,
        [{"file": f} for f in ["src/foo.py"]],
    )
    # Seed final-findings so the stage has something to append to.
    builder.write_json(artifacts.final, {"summary": "ok", "findings": []})
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
    return ctx


class TestAcceptanceCriteriaCoverageStage:
    def test_appends_finding_for_uncovered_ac(self, tmp_path):
        wi = {
            "id": 7,
            "type": "User Story",
            "title": "Charge flow",
            "description": "...",
            "acceptanceCriteria": "Update src/payments/refund.ts to validate input",
        }
        ctx = _stage_ctx(
            tmp_path,
            work_items=[wi],
            diff_text="+ x = 1\n",  # doesn't touch refund.ts
        )
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        assert result.details["uncovered"] == 1
        assert result.details["appended"] == 1

        final = builder.read_json(ctx.artifacts.final)
        assert len(final["findings"]) == 1
        f = final["findings"][0]
        assert f["file"] is None
        assert f["line"] is None
        assert f["severity"] == "major"
        assert "Work item #7" in f["title"]
        assert "src/payments/refund.ts" in f["message"]

    def test_no_finding_when_all_covered(self, tmp_path):
        wi = {
            "id": 7,
            "type": "User Story",
            "title": "Charge flow",
            "description": "...",
            "acceptanceCriteria": "Update src/foo.py to handle the new field",
        }
        ctx = _stage_ctx(
            tmp_path,
            work_items=[wi],
            diff_text="+ x = 1\n",
        )
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        assert result.details["uncovered"] == 0
        assert builder.read_json(ctx.artifacts.final)["findings"] == []

    def test_skips_when_no_work_items(self, tmp_path):
        ctx = _stage_ctx(tmp_path, work_items=[], diff_text="+ x\n")
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        assert result.details["skipped"] == "no work items"

    def test_skips_when_no_diff(self, tmp_path):
        wi = {"id": 1, "title": "x", "acceptanceCriteria": "Update src/foo.py"}
        ctx = _stage_ctx(tmp_path, work_items=[wi], diff_text="")
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        assert result.details["skipped"] == "no diff on disk"

    def test_disabled_by_env_var(self, tmp_path, monkeypatch):
        monkeypatch.setenv("AC_COVERAGE_CHECK", "0")
        wi = {"id": 7, "title": "x", "acceptanceCriteria": "Update src/missing.py"}
        ctx = _stage_ctx(tmp_path, work_items=[wi], diff_text="+ x\n")
        from reviewforge.pipeline.stage import StageStatus
        # Stage returns SKIPPED (status) via should_run returning False.
        # But the stage itself short-circuits via should_run before run().
        # We test via should_run.
        assert not AcceptanceCriteriaCoverageStage().should_run(ctx)

    def test_dry_run_can_be_disabled(self, tmp_path, monkeypatch):
        monkeypatch.setenv("AC_COVERAGE_DRY_RUN", "0")
        wi = {"id": 7, "title": "x", "acceptanceCriteria": "Update src/missing.py"}
        ctx = _stage_ctx(tmp_path, work_items=[wi], diff_text="+ x\n", dry_run=True)
        # Stage's should_run returns False when dry-run + opt-out.
        assert not AcceptanceCriteriaCoverageStage().should_run(ctx)

    def test_includes_existing_findings_in_final(self, tmp_path):
        wi = {"id": 7, "title": "x", "acceptanceCriteria": "Update src/missing.py"}
        ctx = _stage_ctx(tmp_path, work_items=[wi], diff_text="+ x\n")
        # Pre-seed an existing finding.
        builder.write_json(
            ctx.artifacts.final,
            {"summary": "ok", "findings": [
                {"file": "x.py", "line": 1, "severity": "nit", "title": "old", "message": "m"}
            ]},
        )
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        final = builder.read_json(ctx.artifacts.final)
        assert len(final["findings"]) == 2
        assert final["findings"][0]["title"] == "old"
        assert "Work item #7" in final["findings"][1]["title"]

    def test_severity_findings_dropped_when_final_missing(self, tmp_path):
        # Backstop: if final is missing but severity exists, fall back.
        wi = {"id": 7, "title": "x", "acceptanceCriteria": "Update src/missing.py"}
        ctx = _stage_ctx(tmp_path, work_items=[wi], diff_text="+ x\n")
        ctx.artifacts.final.unlink()
        builder.write_json(
            ctx.artifacts.severity,
            {"summary": "ok", "findings": [
                {"file": "x.py", "line": 1, "severity": "nit", "title": "t", "message": "m"}
            ]},
        )
        result = AcceptanceCriteriaCoverageStage()(ctx)
        assert result.status == "ok"
        # Original finding preserved + AC coverage finding appended.
        final = builder.read_json(ctx.artifacts.final)
        assert len(final["findings"]) == 2