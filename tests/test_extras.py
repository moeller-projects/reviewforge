"""Targeted tests to push coverage above 95%."""
from __future__ import annotations

import json
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from auto_pr_reviewer.ai.runner import PiRunner  # noqa: E402
from auto_pr_reviewer.artifacts import builder, manager, summary as art_summary  # noqa: E402
from auto_pr_reviewer.config import Config  # noqa: E402
from auto_pr_reviewer.pipeline import stage as pstage  # noqa: E402
from auto_pr_reviewer.pipeline import validation as pvalidation  # noqa: E402
from auto_pr_reviewer.pipeline.stage import (  # noqa: E402
    Stage,
    StageContext,
    StageStatus,
    run_stages,
)
from auto_pr_reviewer.pipeline.schemas import (  # noqa: E402
    Confidence,
    ContextBasis,
    ContextDigest,
    ContextPlan,
    Evidence,
    Finding,
    Intent,
    ReviewDoc,
    Severity,
    load_and_validate,
    validate_payload,
)


# ---------------------------------------------------------------------------
# AI runner — edge cases
# ---------------------------------------------------------------------------


class TestPiRunnerEdgeCases:
    def _cfg(self):
        return SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=False, pi_session_clear=False, pi_session_id=None,
        )

    def test_timeout_raises_systemexit(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())

        def fake_run(*a, **k):
            raise subprocess.TimeoutExpired(cmd=a[0] if a else "", timeout=5)

        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        with pytest.raises(SystemExit) as exc:
            runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "timed out" in str(exc.value)

    def test_nonzero_returncode_raises(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 7, b"", b"err"),
        )
        with pytest.raises(SystemExit) as exc:
            runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "exited 7" in str(exc.value)

    def test_empty_output_raises(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"", b""),
        )
        with pytest.raises(SystemExit) as exc:
            runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "produced no output" in str(exc.value)

    def test_repair_call_failure_raises(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())
        calls = []

        def fake_run(cmd, **k):
            calls.append(cmd)
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            return subprocess.CompletedProcess(cmd, 9, b"", b"")

        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        with pytest.raises(SystemExit) as exc:
            runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "repair call failed" in str(exc.value)

    def test_repair_call_invalid_json_raises(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())
        calls = []

        def fake_run(cmd, **k):
            calls.append(cmd)
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            # Repair also returns invalid JSON.
            return subprocess.CompletedProcess(cmd, 0, b"still not json", b"")

        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        with pytest.raises(SystemExit) as exc:
            runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "invalid JSON" in str(exc.value)

    def test_strips_ado_api_key_in_subprocess_env(self, tmp_path, monkeypatch):
        runner = PiRunner(self._cfg())
        seen_env = {}

        def fake_run(cmd, input, stdout, stderr, timeout, env):
            seen_env.update(env)
            return subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")

        monkeypatch.setenv("ADO_API_KEY", "secret")
        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            assert k not in seen_env

    def test_stderr_lines_are_logged(self, tmp_path, monkeypatch, capsys):
        runner = PiRunner(self._cfg())
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b'{"ok": true}', b"line1\nline2"),
        )
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        err = capsys.readouterr().err
        assert "[pi stage] line1" in err
        assert "[pi stage] line2" in err

    def test_session_enabled_uses_session_flag(self, tmp_path, monkeypatch):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=True, pi_session_clear=False, pi_session_id="pr-42-review-r1",
        )
        runner = PiRunner(cfg)
        captured = []
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda cmd, **k: (
                captured.append(cmd)
                or subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")
            ),
        )
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "--session-id" in captured[0]
        assert "pr-42-review-r1" in captured[0]
        assert "--no-session" not in captured[0]
        assert runner.session_id == "pr-42-review-r1"

    def test_session_clear_flag_added(self, tmp_path, monkeypatch):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=True, pi_session_clear=True, pi_session_id="x",
        )
        runner = PiRunner(cfg)
        captured = []
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda cmd, **k: (
                captured.append(cmd)
                or subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")
            ),
        )
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "--clear-session" in captured[0]
        assert "--session-id" in captured[0]

    def test_default_session_id_uses_pr_and_run(self, tmp_path):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=True, pi_session_clear=False, pi_session_id=None,
            pr_id="42", review_run_id="r-1",
        )
        assert PiRunner(cfg).session_id == "pr-42-review-r-1"

    def test_default_session_id_without_run_id(self, tmp_path):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=True, pi_session_clear=False, pi_session_id=None,
            pr_id="42", review_run_id=None,
        )
        assert PiRunner(cfg).session_id == "pr-42-review"

    def test_token_usage_parsed_from_stderr(self, tmp_path, monkeypatch):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=False, pi_session_clear=False, pi_session_id=None,
        )
        runner = PiRunner(cfg)
        stderr = b"[pi] tokens: 1234 in / 567 out\n"
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b'{"ok": true}', stderr),
        )
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert runner.last_tokens == {"in": 1234, "out": 567, "total": 1801}

    def test_repair_call_uses_session_and_empty_stdin(self, tmp_path, monkeypatch):
        cfg = SimpleNamespace(
            pi_model="m", pi_timeout_secs=5, dry_run=True,
            review_prompt_path=Path("/tmp/r.md"),
            intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"),
            severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/s.md"),
            pi_session_enabled=True, pi_session_clear=False, pi_session_id="pr-1",
        )
        runner = PiRunner(cfg)
        calls = []
        def fake_run(cmd, input=b"", **k):
            calls.append((cmd, input))
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            return subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")
        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        # Repair call: same --session, empty stdin (no re-send).
        repair_cmd, repair_input = calls[1]
        assert "--session-id" in repair_cmd
        assert "pr-1" in repair_cmd
        assert repair_input == b""
        assert "return only the json" in repair_cmd[-1].lower()


# ---------------------------------------------------------------------------
# Run summary
# ---------------------------------------------------------------------------


def _cfg(tmp_path):
    files = {}
    for n in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        files[n] = tmp_path / f"{n}.md"
        files[n].write_text("p", encoding="utf-8")
    return Config(
        ado_org="o", ado_project="P", ado_repo_id="R", pr_id="42", ado_token="t",
        source_branch="s", target_branch="t",
        workspace=tmp_path, clone_root=tmp_path, review_language="English",
        review_prompt_path=files["review"], intent_prompt_path=files["intent"],
        context_plan_prompt_path=files["plan"], context_digest_prompt_path=files["digest"],
        verify_prompt_path=files["verify"], severity_prompt_path=files["severity"],
        standards_path=files["standards"],
        pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
        disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
        include_work_items=True, include_existing_comments=True,
        verify_findings=True, force_review=False, review_target_branches="",
        review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id="r",
    )


class TestRunSummary:
    def test_new_run_summary_populates_fields(self, tmp_path):
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        summary = art_summary.new_run_summary(cfg, artifacts)
        assert summary.pr_id == "42"
        assert summary.run_id == "r"
        assert summary.dry_run is True
        assert summary.review_language == "English"
        assert summary.exit_code == 0
        assert summary.stages == []
        assert summary.duration_ms == 0

    def test_safe_count_findings_returns_zero_for_missing_file(self, tmp_path):
        assert art_summary._safe_count_findings(tmp_path / "nope.json") == 0

    def test_safe_count_findings_returns_zero_for_invalid_json(self, tmp_path):
        path = tmp_path / "bad.json"
        path.write_text("not json", encoding="utf-8")
        assert art_summary._safe_count_findings(path) == 0

    def test_safe_count_findings_returns_count(self, tmp_path):
        path = tmp_path / "good.json"
        path.write_text(json.dumps({"summary": "x", "findings": [{"a": 1}, {"b": 2}]}), encoding="utf-8")
        assert art_summary._safe_count_findings(path) == 2

    def test_safe_count_findings_handles_non_list_findings(self, tmp_path):
        path = tmp_path / "odd.json"
        path.write_text(json.dumps({"summary": "x", "findings": "not a list"}), encoding="utf-8")
        assert art_summary._safe_count_findings(path) == 0

    def test_finalize_run_summary_with_real_artifacts(self, tmp_path):
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        # Write finding artifacts with various counts.
        for path, n in [
            (artifacts.candidate, 5),
            (artifacts.verified, 3),
            (artifacts.severity, 2),
            (artifacts.final, 1),
        ]:
            builder.write_json(path, {"summary": "x", "findings": [{"i": i} for i in range(n)]})
        summary = art_summary.new_run_summary(cfg, artifacts)
        out = art_summary.finalize_run_summary(
            summary, cfg=cfg, artifacts=artifacts, posted={"created": 1}, exit_code=0,
        )
        assert out["finding_counts"] == {"candidate": 5, "verified": 3, "severity": 2, "final": 1}
        assert out["posted"] == {"created": 1}
        assert out["exit_code"] == 0
        assert out["duration_ms"] >= 0
        assert out["finished_at"] != ""

    def test_finalize_run_summary_with_skipped_reason(self, tmp_path):
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        summary = art_summary.new_run_summary(cfg, artifacts)
        out = art_summary.finalize_run_summary(
            summary, cfg=cfg, artifacts=artifacts, skipped_reason="PR is draft", exit_code=0,
        )
        assert out["skipped_reason"] == "PR is draft"

    def test_build_run_summary_backward_compat_alias(self, tmp_path):
        # The deprecated alias forwards to finalize_run_summary.
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        summary = art_summary.new_run_summary(cfg, artifacts)
        out = art_summary.build_run_summary(summary, cfg=cfg, artifacts=artifacts)
        assert "stages" in out
        assert "finding_counts" in out


# ---------------------------------------------------------------------------
# Validation — edges
# ---------------------------------------------------------------------------


class TestValidationEdges:
    def test_validate_stage_passes_for_no_op_label(self):
        # Stages without a registered validator pass.
        pvalidation.validate_stage({}, "context collection")  # no validator → noop

    def test_validate_stage_rejects_non_dict(self):
        with pytest.raises(SystemExit):
            pvalidation.validate_stage(["not", "a", "dict"], "intent reconstruction")

    def test_validate_review_doc_rejects_non_dict_finding(self):
        with pytest.raises(SystemExit):
            pvalidation.validate_review_doc(
                {"summary": "x", "findings": ["not a dict"]}
            )

    def test_validate_review_doc_rejects_missing_title(self):
        with pytest.raises(SystemExit):
            pvalidation.validate_review_doc(
                {"summary": "x", "findings": [{"severity": "major", "title": "", "message": "m"}]}
            )

    def test_validate_review_doc_rejects_missing_message(self):
        with pytest.raises(SystemExit):
            pvalidation.validate_review_doc(
                {"summary": "x", "findings": [{"severity": "major", "title": "t", "message": "  "}]}
            )

    def test_validate_review_doc_rejects_non_dict(self):
        with pytest.raises(SystemExit):
            pvalidation.validate_review_doc("not a dict")


# ---------------------------------------------------------------------------
# Pydantic schemas — extra edges
# ---------------------------------------------------------------------------


class TestSchemaEdges:
    def test_intent_rejects_whitespace_only_pr_intent(self):
        with pytest.raises(Exception):
            Intent(pr_intent="   ")

    def test_finding_accepts_optional_fields(self):
        f = Finding(severity="major", title="T", message="M")
        assert f.file is None
        assert f.line is None
        assert f.confidence is None

    def test_finding_normalizes_evidence(self):
        f = Finding(
            severity="major", title="T", message="M",
            evidence=Evidence(changedLines=[1, 2], contextFilesRead=["a"], whyNewInThisPr="x", whyNotIntentional="y"),
        )
        assert f.evidence.changedLines == [1, 2]

    def test_review_doc_with_empty_findings(self):
        doc = ReviewDoc(summary="clean", findings=[])
        assert doc.findings == []

    def test_validate_payload_round_trip(self):
        raw = {"pr_intent": "Fix X", "changed_behaviors": [], "risk_areas": []}
        loaded = validate_payload(Intent, raw)
        assert loaded.pr_intent == "Fix X"

    def test_load_and_validate_invalid_file_raises(self, tmp_path):
        path = tmp_path / "bad.json"
        path.write_text("not json", encoding="utf-8")
        with pytest.raises(json.JSONDecodeError):
            load_and_validate(path, Intent)

    def test_context_plan_default_lists(self):
        cp = ContextPlan()
        assert cp.files_to_read == []
        assert cp.tests_to_inspect == []
        assert cp.searches_to_run == []

    def test_context_digest_default_lists(self):
        d = ContextDigest()
        assert d.relevant_context == []
        assert d.possible_intentional_choices == []
        assert d.context_gaps == []


# ---------------------------------------------------------------------------
# Stage runner — edge cases
# ---------------------------------------------------------------------------


class TestStageRunnerEdges:
    def test_stage_failure_with_custom_exception(self, tmp_path):
        class BadStage(Stage):
            name = "bad"

            def run(self, ctx):
                raise ValueError("oops")

        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        result = BadStage()(ctx)
        assert result.status == StageStatus.FAILED
        assert "ValueError" in result.error
        assert "oops" in result.error

    def test_stage_returns_non_dict(self, tmp_path):
        class ReturnListStage(Stage):
            name = "list"

            def run(self, ctx):
                return ["a", "b"]  # not a dict

        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        result = ReturnListStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"result": ["a", "b"]}

    def test_run_stages_short_circuits_on_failure(self, tmp_path):
        class A(Stage):
            name = "a"
            def run(self, ctx): return {}

        class B(Stage):
            name = "b"
            def run(self, ctx): raise SystemExit("b failed")

        class C(Stage):
            name = "c"
            def run(self, ctx): return {}

        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        results = run_stages([A(), B(), C()], ctx)
        names = [r.name for r in results]
        assert names == ["a", "b"]

    def test_run_stages_continues_past_skipped(self, tmp_path):
        class Skipped(Stage):
            name = "skipped"
            def should_run(self, ctx): return False
            def run(self, ctx): raise AssertionError("should not run")

        class A(Stage):
            name = "a"
            def run(self, ctx): return {}

        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        results = run_stages([Skipped(), A()], ctx)
        assert [r.name for r in results] == ["skipped", "a"]
        assert results[0].status == StageStatus.SKIPPED
        assert results[1].status == StageStatus.OK


# ---------------------------------------------------------------------------
# Artifacts — edges
# ---------------------------------------------------------------------------


class TestArtifactsEdges:
    def test_read_json_empty_file_returns_none(self, tmp_path):
        path = tmp_path / "empty.json"
        path.write_text("", encoding="utf-8")
        assert builder.read_json(path) is None

    def test_read_json_whitespace_only_returns_none(self, tmp_path):
        path = tmp_path / "ws.json"
        path.write_text("   \n", encoding="utf-8")
        assert builder.read_json(path) is None

    def test_changed_files_handles_no_extension(self):
        result = builder.changed_files(["Makefile", "Dockerfile", "noext"])
        assert [r["file"] for r in result] == ["Makefile", "Dockerfile", "noext"]
        for r in result:
            assert r["language"] == "Other"
            assert r["isTest"] is False

    def test_changed_files_classifies_multiple_languages(self):
        result = builder.changed_files([
            "a.py", "b.ts", "c.go", "d.rs", "e.kt",
            "scripts/run.ps1", "module/main.tf",
        ])
        languages = {r["file"]: r["language"] for r in result}
        assert languages["a.py"] == "Python"
        assert languages["b.ts"] == "TypeScript"
        assert languages["c.go"] == "Go"
        assert languages["d.rs"] == "Rust"
        assert languages["e.kt"] == "Kotlin"
        assert languages["scripts/run.ps1"] == "PowerShell"
        assert languages["module/main.tf"] == "HCL"

    def test_create_artifacts_writes_latest_pointer(self, tmp_path):
        from dataclasses import replace
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        latest_path = cfg.review_artifact_root / f"pr-{cfg.pr_id}" / "latest.txt"
        assert latest_path.exists()
        assert latest_path.read_text().strip() == str(artifacts.dir)


# ---------------------------------------------------------------------------
# Chunker — edges
# ---------------------------------------------------------------------------


class TestChunkerEdges:
    def test_no_files_returns_empty(self, tmp_path):
        from auto_pr_reviewer.git import chunker
        state = SimpleNamespace(repo_dir=tmp_path, files=[], range_spec="x..y")
        chunks, truncated = chunker.build_chunks(state, 100)
        assert chunks == []
        assert truncated is False


# ---------------------------------------------------------------------------
# Pipeline context (legacy)
# ---------------------------------------------------------------------------


class TestPipelineContext:
    def test_paths_returns_all_artifact_keys(self, tmp_path):
        from auto_pr_reviewer.pipeline.context import ReviewContext
        cfg = _cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = ReviewContext(cfg=cfg, artifacts=artifacts, pi=MagicMock())
        paths = ctx.paths()
        for key in ("intent", "plan", "collected", "digest", "candidate", "verified", "severity", "final"):
            assert key in paths
        assert paths["intent"] == artifacts.intent

    def test_log_writes_to_stderr(self, capsys):
        from auto_pr_reviewer.pipeline.context import ReviewContext
        cfg = MagicMock()
        artifacts = MagicMock()
        ctx = ReviewContext(cfg=cfg, artifacts=artifacts, pi=MagicMock())
        ctx.log("hello")
        assert "[review] hello" in capsys.readouterr().err
