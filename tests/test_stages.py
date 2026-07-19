"""Comprehensive coverage tests for the pipeline stages.

Each stage is exercised in two ways:

* **Happy path** — set up a real :class:`StageContext` with a real
  :class:`Config` and a mocked :class:`PiRunner` that writes a valid
  JSON document to the expected artifact path. Verify the stage
  mutates the context, returns the expected details, and validates
  the JSON against the stage's schema.
* **Edge cases** — skip conditions, ``state is None``, dry-run,
  chunked review, file path normalization, etc.

These tests are the bulk of the work needed to bring the package
coverage to 95%.
"""
from __future__ import annotations

import json
import shutil
import subprocess
import sys
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
sys.path.insert(0, str(SRC))

from reviewforge.artifacts import builder, manager  # noqa: E402
from reviewforge.config import Config  # noqa: E402
from reviewforge.pipeline.cache import cache_key  # noqa: E402
from reviewforge.pipeline.stage import (  # noqa: E402
    StageContext,
    StageStatus,
)
from reviewforge.pipeline.stages import (  # noqa: E402
    BuildArtifactsStage,
    CalibrateSeverityStage,
    CollectContextStage,
    ContextDigestStage,
    FetchPrMetadataStage,
    PlanContextStage,
    PostToAdoStage,
    PrepareRepositoryStage,
    ReconstructIntentStage,
    ReviewDiffStage,
    VerifyFindingsStage,
)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def cfg(tmp_path: Path) -> Config:
    """A complete, valid :class:`Config` rooted at ``tmp_path``."""
    files: dict[str, Path] = {}
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        p = tmp_path / f"{name}.md"
        p.write_text(f"{name} prompt", encoding="utf-8")
        files[name] = p
    return Config(
        ado_org="contoso",
        ado_project="Payments",
        ado_repo_id="api",
        pr_id="42",
        ado_token="tok",
        source_branch="feature",
        target_branch="main",
        workspace=tmp_path / "workspace",
        clone_root=tmp_path / "workspace",
        review_language="English",
        review_prompt_path=files["review"],
        intent_prompt_path=files["intent"],
        context_plan_prompt_path=files["plan"],
        context_digest_prompt_path=files["digest"],
        verify_prompt_path=files["verify"],
        severity_prompt_path=files["severity"],
        standards_path=files["standards"],
        pi_model="test/model",
        max_diff_bytes=100,
        chunk_trigger_diff_bytes=100,
        disable_chunk_review=False,
        pi_timeout_secs=5,
        dry_run=True,
        include_work_items=True,
        include_existing_comments=True,
        verify_findings=True,
        force_review=False,
        review_target_branches="",
        review_artifact_dir=None,
        review_artifact_root=tmp_path / "artifacts",
        review_run_id="run-1",
    )


@pytest.fixture
def artifacts(cfg: Config):
    """A real artifact directory tree for ``cfg``."""
    return manager.create(cfg)


def _make_pi(artifact_paths: dict[str, Path], payload: Any) -> MagicMock:
    """Build a mocked :class:`PiRunner` that writes ``payload`` to one of ``artifact_paths``."""
    pi = MagicMock()
    pi_model = MagicMock()
    target: list[Path] = []

    def record(prompt, stdin, out, stage):
        target.append(out)
        builder.write_json(out, payload)

    pi.run_json.side_effect = record
    return pi


def _stage_context(
    cfg: Config,
    artifacts,
    pi,
    *,
    state=None,
    metadata=None,
    wi_context=None,
    wi_comments_context=None,
    thread_context=None,
) -> StageContext:
    """Build a populated :class:`StageContext`.

    Writes a placeholder ``metadata.json`` so the prompt-assembly helpers
    can read it (in production this file is created by
    :class:`FetchPrMetadataStage`). Pre-populates ``ctx.files_text``
    with a placeholder so downstream stages don't see ``AttributeError``.

    The ``wi_context`` / ``wi_comments_context`` / ``thread_context``
    parameters mirror what :class:`FetchPrMetadataStage` populates in
    ``ctx.extras``. When ``None`` (the default), the fixture writes empty
    lists — matching the legacy / pre-loader behaviour, so tests that
    don't care about work items keep working unchanged. Tests that need
    the new loader behaviour pass explicit lists.
    """
    if metadata is None and not artifacts.metadata.exists():
        builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=pi)
    state_files = getattr(state, "files", None) if state else None
    ctx.files_text = ("\n".join(state_files) + "\n") if state_files else "a.py\n"
    ctx.extras["paths"] = {
        "intent": artifacts.intent,
        "plan": artifacts.plan,
        "collected": artifacts.collected,
        "digest": artifacts.digest,
        "candidate": artifacts.candidate,
        "verified": artifacts.verified,
        "severity": artifacts.severity,
        "final": artifacts.final,
    }
    # Default to empty lists (legacy behaviour). Tests that exercise the
    # post-loader pipeline pass explicit populated values.
    ctx.extras["wi_context"] = [] if wi_context is None else wi_context
    ctx.extras["wi_comments_context"] = (
        [] if wi_comments_context is None else wi_comments_context
    )
    ctx.extras["thread_context"] = (
        [] if thread_context is None else thread_context
    )
    if metadata is not None:
        ctx.metadata = metadata
    return ctx


# ---------------------------------------------------------------------------
# FetchPrMetadataStage
# ---------------------------------------------------------------------------


class TestFetchPrMetadataStage:
    def test_writes_metadata_when_helper_runs(self, cfg, artifacts, monkeypatch):
        builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False, "sourceRefName": "refs/heads/feature"})
        pi = MagicMock()
        ctx = _stage_context(cfg, artifacts, pi)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.fetch_pr_metadata.call_helper",
            lambda *a, **k: None,
        )
        result = FetchPrMetadataStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {
            "pr_id": "42",
            "status": "active",
            "is_draft": False,
            "work_items_loaded": 0,
            "threads_loaded": 0,
        }
        assert ctx.metadata["status"] == "active"

    def test_returns_cached_when_metadata_already_set(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock(), metadata={"status": "active"})
        result = FetchPrMetadataStage()(ctx)
        assert result.details == {"cached": True, "pr_id": "42"}

    def test_loads_fetched_context_into_extras(self, cfg, artifacts, monkeypatch):
        # The fetch-context subprocess writes four files. The stage must
        # load them back into ctx.extras so downstream stages see the
        # work items and threads (without this, the work-item-aware
        # prompts operate on empty lists — see
        # docs/design/work-item-verification-false-positives.md).
        builder.write_json(
            artifacts.metadata, {"status": "active", "isDraft": False}
        )
        builder.write_json(
            artifacts.work_items,
            [
                {
                    "id": 42,
                    "type": "Bug",
                    "title": "Charge fails on retry",
                    "state": "Active",
                    "description": "Retries fail with 502.",
                    "acceptanceCriteria": "Retry returns 200 within 3s.",
                }
            ],
        )
        builder.write_json(
            artifacts.work_items.with_name("work-item-comments.json"),
            [
                {
                    "workItemId": "42",
                    "comments": [
                        {
                            "id": 1,
                            "author": "pm@example.com",
                            "text": "Retry is in scope for this PR.",
                        }
                    ],
                }
            ],
        )
        builder.write_json(
            artifacts.threads,
            [
                {
                    "id": 7,
                    "status": "active",
                    "filePath": None,
                    "line": None,
                    "firstComment": "Already discussed in the sync.",
                    "author": "reviewer@example.com",
                }
            ],
        )

        ctx = _stage_context(cfg, artifacts, MagicMock())
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.fetch_pr_metadata.call_helper",
            lambda *a, **k: None,
        )

        result = FetchPrMetadataStage()(ctx)

        assert result.status == StageStatus.OK
        assert result.details["work_items_loaded"] == 1
        assert result.details["threads_loaded"] == 1
        assert len(ctx.extras["wi_context"]) == 1
        assert ctx.extras["wi_context"][0]["id"] == 42
        assert len(ctx.extras["wi_comments_context"]) == 1
        assert ctx.extras["wi_comments_context"][0]["workItemId"] == "42"
        assert len(ctx.extras["thread_context"]) == 1
        assert ctx.extras["thread_context"][0]["id"] == 7

    def test_cached_metadata_skips_loader(self, cfg, artifacts, monkeypatch):
        # When the metadata is already cached (rerun with cached state),
        # the fetch-context subprocess is NOT called and the loader is
        # not run. The existing ctx.extras (if any) is preserved. This
        # matches the cached fast path.
        ctx = _stage_context(
            cfg,
            artifacts,
            MagicMock(),
            metadata={"status": "active"},
            wi_context=[{"id": 99, "type": "Task", "title": "cached", "state": "Active"}],
        )
        # Ensure no fetch artifacts exist on disk; the loader would
        # otherwise try to read them.
        for p in (
            artifacts.work_items,
            artifacts.work_items.with_name("work-item-comments.json"),
            artifacts.threads,
        ):
            if p.exists():
                p.unlink()
        # Should not be called.
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.fetch_pr_metadata.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = FetchPrMetadataStage()(ctx)
        assert result.details == {"cached": True, "pr_id": "42"}
        assert called == []  # subprocess not invoked
        # Pre-populated extras are preserved (caller is responsible for
        # what goes in them on a cached rerun).
        assert ctx.extras["wi_context"][0]["id"] == 99

    def test_loader_skips_missing_files(self, cfg, artifacts, monkeypatch):
        # If the fetch-context subprocess failed and only some files
        # exist on disk, the loader should load what it can and leave
        # the rest at the default empty list. The pipeline still runs.
        builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
        # Only work-items.json present; threads and comments missing.
        builder.write_json(
            artifacts.work_items,
            [{"id": 1, "type": "Task", "title": "x", "state": "Active"}],
        )
        # threads and work-item-comments intentionally not written.
        ctx = _stage_context(cfg, artifacts, MagicMock())
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.fetch_pr_metadata.call_helper",
            lambda *a, **k: None,
        )
        result = FetchPrMetadataStage()(ctx)
        assert result.status == StageStatus.OK
        assert len(ctx.extras["wi_context"]) == 1
        assert ctx.extras["wi_comments_context"] == []
        assert ctx.extras["thread_context"] == []
        assert result.details["work_items_loaded"] == 1
        assert result.details["threads_loaded"] == 0

    def test_loader_ignores_malformed_files(self, cfg, artifacts, monkeypatch):
        # A malformed file (not a JSON list) should be skipped, not crash
        # the stage. A dict in place of the expected list is the most
        # likely failure mode (a future fetch-context refactor that
        # accidentally writes context.json instead of work-items.json).
        builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
        builder.write_json(artifacts.work_items, {"not": "a list"})  # wrong shape
        builder.write_json(artifacts.threads, "also not a list")
        ctx = _stage_context(cfg, artifacts, MagicMock())
        monkeypatch.setattr(
            "auto_pr_pr_reviewer.pipeline.stages.fetch_pr_metadata.call_helper".replace(
                "auto_pr_pr_reviewer", "reviewforge"
            ),
            lambda *a, **k: None,
        )
        result = FetchPrMetadataStage()(ctx)
        assert result.status == StageStatus.OK
        assert ctx.extras["wi_context"] == []
        assert ctx.extras["thread_context"] == []


# ---------------------------------------------------------------------------
# _load_fetched_context helper (unit tests)
# ---------------------------------------------------------------------------


class TestLoadFetchedContext:
    def test_loads_all_three_files(self, artifacts):
        from reviewforge.pipeline.stages.fetch_pr_metadata import (
            _load_fetched_context,
        )

        builder.write_json(
            artifacts.work_items,
            [{"id": 1, "type": "Bug", "title": "x", "state": "Active"}],
        )
        builder.write_json(
            artifacts.work_items.with_name("work-item-comments.json"),
            [{"workItemId": "1", "comments": []}],
        )
        builder.write_json(
            artifacts.threads,
            [{"id": 2, "status": "active"}],
        )
        result = _load_fetched_context(artifacts)
        assert "wi_context" in result
        assert "wi_comments_context" in result
        assert "thread_context" in result
        assert result["wi_context"][0]["id"] == 1
        assert result["thread_context"][0]["id"] == 2

    def test_missing_files_returns_empty_dict(self, artifacts):
        from reviewforge.pipeline.stages.fetch_pr_metadata import (
            _load_fetched_context,
        )

        result = _load_fetched_context(artifacts)
        assert result == {}

    def test_malformed_json_skipped(self, artifacts):
        from reviewforge.pipeline.stages.fetch_pr_metadata import (
            _load_fetched_context,
        )

        artifacts.work_items.write_text("not json at all", encoding="utf-8")
        # threads is valid; the helper should still load it.
        builder.write_json(artifacts.threads, [{"id": 9}])
        result = _load_fetched_context(artifacts)
        assert "wi_context" not in result
        assert "thread_context" in result
        assert result["thread_context"][0]["id"] == 9


# ---------------------------------------------------------------------------
# PrepareRepositoryStage
# ---------------------------------------------------------------------------


class TestPrepareRepositoryStage:
    def _fake_state(self, tmp_path: Path, diff_text: str, files: list[str]):
        return SimpleNamespace(
            repo_dir=tmp_path,
            files=files,
            diff_text=diff_text,
            range_spec="base..head",
            target_branch="main",
            source_branch="feature",
            target_commit="t",
            source_commit="s",
            base_commit="b",
        )

    def test_writes_diff_and_changed_files(self, cfg, artifacts, monkeypatch):
        pi = MagicMock()
        ctx = _stage_context(cfg, artifacts, pi)
        fake_state = self._fake_state(artifacts.dir, "diff --git a/x", ["x.py"])
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.prepare_repository.resolve_branches",
            lambda c: ("feature", "main"),
        )
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.prepare_repository.git_ops.prepare_repo",
            lambda c, s, t: fake_state,
        )
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.prepare_repository.git_ops.run_git",
            lambda *a, **k: "abc1234 commit message\n",
        )
        result = PrepareRepositoryStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {
            "files": 1, "diff_bytes": 14,
            "source_branch": "feature", "target_branch": "main",
        }
        assert ctx.state is fake_state
        assert ctx.files_text == "x.py\n"
        assert artifacts.diff.read_text() == "diff --git a/x"
        assert builder.read_json(artifacts.changed_files) == [
            {"file": "x.py", "language": "Python", "isTest": False}
        ]
        assert "commit message" in artifacts.commits.read_text()


# ---------------------------------------------------------------------------
# BuildArtifactsStage
# ---------------------------------------------------------------------------


class TestBuildArtifactsStage:
    def test_writes_combined_system_prompt(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock())
        result = BuildArtifactsStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details["system_prompt_path"].endswith("review-system.combined.md")
        assert result.details["system_prompt_bytes"] > 0
        # File was written.
        assert artifacts.system_prompt.exists()
        # Legacy compat hook was populated.
        assert ctx.extras.get("system_prompt") == artifacts.system_prompt.read_text()

    def test_preserves_existing_extras_system_prompt(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock())
        ctx.extras["system_prompt"] = "preexisting"
        BuildArtifactsStage()(ctx)
        # setdefault must not overwrite.
        assert ctx.extras["system_prompt"] == "preexisting"


# ---------------------------------------------------------------------------
# ReconstructIntentStage
# ---------------------------------------------------------------------------


class TestReconstructIntentStage:
    INTENT = {
        "pr_intent": "Refactor auth to use JWT tokens",
        "changed_behaviors": ["login now returns a JWT", "logout invalidates the token"],
        "risk_areas": ["token storage"],
    }

    def test_writes_intent_and_records_details(self, cfg, artifacts):
        pi = _make_pi({"intent": artifacts.intent}, self.INTENT)
        state = SimpleNamespace(diff_text="diff", target_branch="main", source_branch="feature",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = ReconstructIntentStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details["pr_intent"].startswith("Refactor auth")
        assert result.details["risk_areas"] == 1
        assert ctx.intent is not None
        assert builder.read_json(artifacts.intent) == self.INTENT

    def test_fails_on_invalid_intent(self, cfg, artifacts):
        pi = _make_pi({"intent": artifacts.intent}, {"pr_intent": "x"})
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = ReconstructIntentStage()(ctx)
        assert result.status == StageStatus.FAILED
        assert "intent" in result.error.lower()

    def test_runs_without_state(self, cfg, artifacts):
        pi = _make_pi({"intent": artifacts.intent}, self.INTENT)
        ctx = _stage_context(cfg, artifacts, pi, state=None)
        result = ReconstructIntentStage()(ctx)
        assert result.status == StageStatus.OK


# ---------------------------------------------------------------------------
# PlanContextStage
# ---------------------------------------------------------------------------


class TestPlanContextStage:
    PLAN = {
        "files_to_read": [{"path": "src/a.py", "reason": "changed"}],
        "searches_to_run": [{"query": "TODO", "reason": "audit"}],
        "tests_to_inspect": ["tests/test_a.py"],
    }

    def test_writes_plan_and_details(self, cfg, artifacts):
        pi = _make_pi({"plan": artifacts.plan}, self.PLAN)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = PlanContextStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"files_to_read": 1, "searches": 1, "tests": 1}
        assert ctx.plan is not None

    def test_fails_on_invalid_plan(self, cfg, artifacts):
        pi = _make_pi({"plan": artifacts.plan}, {"files_to_read": []})
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = PlanContextStage()(ctx)
        assert result.status == StageStatus.FAILED


# ---------------------------------------------------------------------------
# ContextDigestStage
# ---------------------------------------------------------------------------


class TestContextDigestStage:
    DIGEST = {
        "relevant_context": [{"note": "uses bcrypt"}],
        "possible_intentional_choices": [{"choice": "kept synchronous login"}],
        "context_gaps": [],
    }

    def test_writes_digest_and_details(self, cfg, artifacts):
        pi = _make_pi({"digest": artifacts.digest}, self.DIGEST)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = ContextDigestStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"relevant_context": 1, "intentional_choices": 1}
        assert ctx.digest is not None

    def test_fails_on_invalid_digest(self, cfg, artifacts):
        pi = _make_pi({"digest": artifacts.digest}, {"relevant_context": []})
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = ContextDigestStage()(ctx)
        assert result.status == StageStatus.FAILED


# ---------------------------------------------------------------------------
# CalibrateSeverityStage
# ---------------------------------------------------------------------------


class TestCalibrateSeverityStage:
    DOC = {
        "summary": "calibrated",
        "findings": [
            {"severity": "major", "title": "T1", "message": "M1"},
            {"severity": "nit", "title": "T2", "message": "M2"},
        ],
    }

    def test_writes_severity_and_details(self, cfg, artifacts):
        pi = _make_pi({"severity": artifacts.severity}, self.DOC)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = CalibrateSeverityStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 2}
        assert ctx.severity is not None
    def test_parallel_workers_use_unique_pi_sessions(self, cfg, artifacts):
        cfg = replace(cfg, pi_session_id="pr-42-review-run-1")
        verified = {
            "summary": "verified",
            "findings": [
                {"severity": "major", "title": "T1", "message": "M1"},
                {"severity": "minor", "title": "T2", "message": "M2"},
            ],
        }
        builder.write_json(artifacts.verified, verified)

        class PiRunner:
            session_ids: list[str | None] = []

            def __init__(self, runner_cfg):
                self.cfg = runner_cfg
                self.session_id = runner_cfg.pi_session_id or "base"
                self.last_tokens = {}

            def run_json(self, prompt, stdin, out, stage):
                self.session_ids.append(self.cfg.pi_session_id)
                builder.write_json(
                    out,
                    {
                        "summary": "calibrated",
                        "findings": [
                            {
                                "severity": "major",
                                "title": "calibrated",
                                "message": "M",
                            }
                        ],
                    },
                )

        pi = PiRunner(cfg)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = CalibrateSeverityStage()(ctx)

        assert result.status == StageStatus.OK
        assert set(pi.session_ids) == {
            "pr-42-review-run-1-severity-1",
            "pr-42-review-run-1-severity-2",
        }

    def test_preserves_verified_finding_when_worker_output_is_malformed(self, cfg, artifacts):
        verified = {
            "summary": "verified",
            "findings": [
                {"severity": "major", "title": "Original 1", "message": "M1"},
                {"severity": "minor", "title": "Original 2", "message": "M2"},
            ],
        }
        builder.write_json(artifacts.verified, verified)

        class Pi:
            def run_json(self, prompt, stdin, out, stage):
                builder.write_json(
                    out,
                    {
                        "summary": "calibrated",
                        "findings": [
                            {"severity": "major", "message": "M1"}
                            if out.name == "severity-1.json"
                            else {"severity": "blocker", "title": "Calibrated 2", "message": "M2"}
                        ],
                    },
                )

        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, Pi(), state=state)
        result = CalibrateSeverityStage()(ctx)

        assert result.status == StageStatus.OK
        assert {f["title"]: f["severity"] for f in ctx.severity["findings"]} == {
            "Original 1": "major",
            "Calibrated 2": "blocker",
        }

    def test_fails_on_invalid_severity_doc(self, cfg, artifacts):
        bad = {"summary": "x", "findings": [{"severity": "critical", "title": "T", "message": "M"}]}
        pi = _make_pi({"severity": artifacts.severity}, bad)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = CalibrateSeverityStage()(ctx)
        assert result.status == StageStatus.FAILED


# ---------------------------------------------------------------------------
# VerifyFindingsStage
# ---------------------------------------------------------------------------


class TestVerifyFindingsStage:
    DOC = {
        "summary": "verified",
        "findings": [{"severity": "major", "title": "T1", "message": "M1"}],
    }

    def test_runs_pi_when_verify_findings_enabled(self, cfg, artifacts):
        cfg = replace(cfg, verify_findings=True)
        pi = _make_pi({"verified": artifacts.verified}, self.DOC)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 1}
        assert pi.run_json.called
        assert ctx.verified is not None
    def test_parallel_workers_use_unique_pi_sessions(self, cfg, artifacts):
        cfg = replace(cfg, pi_session_id="pr-42-review-run-1")
        candidate = {
            "summary": "candidates",
            "findings": [
                {"severity": "major", "title": "T1", "message": "M1"},
                {"severity": "minor", "title": "T2", "message": "M2"},
            ],
        }
        builder.write_json(artifacts.candidate, candidate)

        class PiRunner:
            session_ids: list[str | None] = []

            def __init__(self, runner_cfg):
                self.cfg = runner_cfg
                self.session_id = runner_cfg.pi_session_id or "base"
                self.last_tokens = {}

            def run_json(self, prompt, stdin, out, stage):
                self.session_ids.append(self.cfg.pi_session_id)
                builder.write_json(
                    out,
                    {
                        "summary": "verified",
                        "findings": [
                            {
                                "severity": "major",
                                "title": "verified",
                                "message": "M",
                            }
                        ],
                    },
                )

        pi = PiRunner(cfg)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = VerifyFindingsStage()(ctx)

        assert result.status == StageStatus.OK
        assert set(pi.session_ids) == {
            "pr-42-review-run-1-verify-1",
            "pr-42-review-run-1-verify-2",
        }


    def test_skips_pi_when_verify_findings_disabled(self, cfg, artifacts):
        cfg = replace(cfg, verify_findings=False)
        pi = MagicMock()
        builder.write_json(artifacts.candidate, self.DOC)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 1, "skipped": True}
        assert not pi.run_json.called
        assert ctx.verified == self.DOC
        assert builder.read_json(artifacts.verified) == self.DOC

    def test_single_validation_logs_invalid_finding(self, cfg, artifacts, capsys):
        cfg = replace(cfg, verify_findings=True)
        bad = {"summary": "x", "findings": [{"severity": "major", "title": "T", "message": ""}]}
        pi = _make_pi({"verified": artifacts.verified}, bad)
        builder.write_json(artifacts.candidate, {
            "summary": "candidates",
            "findings": [{"severity": "major", "title": "T", "message": "M"}],
        })
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.FAILED
        stderr = capsys.readouterr().err
        assert "verification output failed validation" in stderr
        assert '"message": ""' in stderr

    def test_fails_on_invalid_verified_doc(self, cfg, artifacts):
        cfg = replace(cfg, verify_findings=True)
        bad = {"summary": "x", "findings": [{"severity": "critical", "title": "T", "message": "M"}]}
        pi = _make_pi({"verified": artifacts.verified}, bad)
        state = SimpleNamespace(diff_text="d", target_branch="m", source_branch="f",
                                target_commit="t", source_commit="s", base_commit="b")
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.FAILED


# ---------------------------------------------------------------------------
# ReviewDiffStage
# ---------------------------------------------------------------------------


class TestReviewDiffStage:
    DOC = {
        "summary": "found something",
        "findings": [
            {"severity": "major", "title": "T1", "message": "M1",
             "file": "/src/a.py", "line": 5},
        ],
    }

    def _state(self, diff_text: str, files: list[str]):
        return SimpleNamespace(
            diff_text=diff_text, files=files, range_spec="x..y",
            target_branch="m", source_branch="f",
            target_commit="t", source_commit="s", base_commit="b",
        )

    def test_skips_when_state_is_none(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=None)
        result = ReviewDiffStage()(ctx)
        # ``should_run`` returns False → status SKIPPED, not OK.
        assert result.status == StageStatus.SKIPPED
        assert result.details == {}

    def test_skips_when_diff_is_empty(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=self._state("", []))
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.SKIPPED

    def test_single_pass_under_trigger(self, cfg, artifacts):
        cfg = replace(cfg, chunk_trigger_diff_bytes=10_000, max_diff_bytes=10_000)
        pi = _make_pi({"candidate": artifacts.candidate}, self.DOC)
        state = self._state("small diff", ["a.py"])
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        ctx.files_text = "a.py\n"
        ctx.extras["system_prompt"] = "sys"
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 1, "chunks": 0}
        # System prompt file was rewritten.
        assert artifacts.system_prompt.read_text() == "sys"
        # File path with leading slash was normalized.
        doc = builder.read_json(artifacts.candidate)
        assert doc["findings"][0]["file"] == "src/a.py"

    def test_disable_chunk_review_forces_single_pass(self, cfg, artifacts):
        cfg = replace(cfg, disable_chunk_review=True, chunk_trigger_diff_bytes=1)
        pi = _make_pi({"candidate": artifacts.candidate}, self.DOC)
        state = self._state("big diff", ["a.py"])
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        ctx.files_text = "a.py\n"
        ctx.extras["system_prompt"] = "sys"
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details["chunks"] == 0

    def test_chunked_review_dedupes_findings(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, chunk_trigger_diff_bytes=1, max_diff_bytes=1)
        pi = MagicMock()
        # Two chunks with overlapping findings; should dedupe.
        calls = []

        def fake_run_json(prompt, stdin, out, stage):
            calls.append(out)
            builder.write_json(out, self.DOC)

        pi.run_json.side_effect = fake_run_json
        state = self._state("big diff with many files", ["a.py", "b.py"])
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.review_diff.build_chunks",
            lambda _state, _max: ([
                SimpleNamespace(diff_text="d1", files_text="a.py\n", truncated=False),
                SimpleNamespace(diff_text="d2", files_text="b.py\n", truncated=False),
            ], False),
        )
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        ctx.files_text = "a.py\nb.py\n"
        ctx.extras["system_prompt"] = "sys"
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.OK
        assert len(calls) == 2
        assert result.details["chunks"] == 1
        doc = builder.read_json(artifacts.candidate)
        assert len(doc["findings"]) == 1  # dedup'd across chunks
        assert "(across 2 diff chunks)" in doc["summary"]
    def test_chunk_workers_use_unique_pi_sessions(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, chunk_trigger_diff_bytes=1, max_diff_bytes=1,
                      pi_session_id="pr-42-review-run-1")

        class PiRunner:
            session_ids: list[str | None] = []

            def __init__(self, runner_cfg):
                self.cfg = runner_cfg
                self.session_id = runner_cfg.pi_session_id or "base"
                self.last_tokens = {}

            def run_json(self, prompt, stdin, out, stage):
                self.session_ids.append(self.cfg.pi_session_id)
                builder.write_json(out, self.DOC)

            DOC = {
                "summary": "chunk review",
                "findings": [
                    {"severity": "major", "title": "T", "message": "M"},
                ],
            }

        pi = PiRunner(cfg)
        state = self._state("big diff with many files", ["a.py", "b.py"])
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.review_diff.build_chunks",
            lambda _state, _max: ([
                SimpleNamespace(diff_text="d1", files_text="a.py\n", truncated=False),
                SimpleNamespace(diff_text="d2", files_text="b.py\n", truncated=False),
            ], False),
        )
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        ctx.files_text = "a.py\nb.py\n"
        ctx.extras["system_prompt"] = "sys"
        result = ReviewDiffStage()(ctx)

        assert result.status == StageStatus.OK
        assert set(pi.session_ids) == {
            "pr-42-review-run-1-chunk-1",
            "pr-42-review-run-1-chunk-2",
        }


    def test_chunked_review_empty_summaries_uses_default(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, chunk_trigger_diff_bytes=1, max_diff_bytes=1)
        pi = MagicMock()

        def fake_run_json(prompt, stdin, out, stage):
            builder.write_json(out, {"summary": "", "findings": []})

        pi.run_json.side_effect = fake_run_json
        state = self._state("big", ["a.py", "b.py"])
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.review_diff.build_chunks",
            lambda _state, _max: ([
                SimpleNamespace(diff_text="d1", files_text="a.py\n", truncated=False),
                SimpleNamespace(diff_text="d2", files_text="b.py\n", truncated=False),
            ], False),
        )
        ctx = _stage_context(cfg, artifacts, pi, state=state)
        ctx.files_text = "a.py\nb.py\n"
        ctx.extras["system_prompt"] = "sys"
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.candidate)
        assert "Reviewed 2 diff chunks" in doc["summary"]


# ---------------------------------------------------------------------------
# PostToAdoStage
# ---------------------------------------------------------------------------


class TestPostToAdoStage:
    DOC = {
        "summary": "ok",
        "findings": [
            {
                "severity": "major",
                "title": "T",
                "message": "M",
                "suggestion": "Fix the issue.",
                "evidence": {
                    "changedLines": [1],
                    "whyNewInThisPr": "The changed line introduces it.",
                },
            }
        ],
    }

    def test_dry_run_short_circuits(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, dry_run=True)
        builder.write_json(artifacts.severity, self.DOC)
        ctx = _stage_context(cfg, artifacts, MagicMock())
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"dry_run": True, "findings": 1}
        assert called == []
        assert ctx.posted.get("dry_run") == 1
        assert builder.read_json(artifacts.posted) == ctx.posted
        # final-findings.json is a copy of severity.
        assert builder.read_json(artifacts.final) == self.DOC
        assert ctx.final is not None

    def test_preserves_existing_final_findings(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, dry_run=False)
        builder.write_json(artifacts.severity, {"summary": "ok", "findings": []})
        builder.write_json(
            artifacts.final,
            {
                "summary": "ok",
                "findings": [
                    {
                        "severity": "major",
                        "title": "AC coverage",
                        "message": "uncovered AC",
                        "suggestion": "Address the acceptance criterion.",
                        "evidence": {
                            "workItems": ["#7"],
                            "classification": "work-item",
                            "whyNewInThisPr": "The criterion is not covered.",
                        },
                    }
                ],
            },
        )
        artifacts.dir.joinpath("posted-findings.json").write_text(
            json.dumps({"created": 1, "skipped": 0, "comments": []}),
            encoding="utf-8",
        )
        ctx = _stage_context(cfg, artifacts, MagicMock())
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert len(called) == 1
        assert result.details == {
            "posted": {"created": 1, "skipped": 0, "comments": []},
            "findings": 1,
        }
        final = builder.read_json(artifacts.final)
        assert final["findings"][0]["title"] == "AC coverage"

    def test_posting_calls_helper_and_records(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, dry_run=False)
        builder.write_json(artifacts.severity, self.DOC)
        artifacts.dir.joinpath("posted-findings.json").write_text(
            json.dumps({"created": 1, "skipped": 0, "comments": []}),
            encoding="utf-8",
        )
        ctx = _stage_context(cfg, artifacts, MagicMock())
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert len(called) == 1
        assert called[0][0][1] == "post-findings"
        assert result.details == {"posted": {"created": 1, "skipped": 0, "comments": []}, "findings": 1}
        assert ctx.posted == {"created": 1, "skipped": 0, "comments": []}

    def test_loads_existing_severity_from_artifact(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, dry_run=True)
        builder.write_json(artifacts.severity, self.DOC)
        ctx = _stage_context(cfg, artifacts, MagicMock())
        ctx.severity = None  # Force stage to read from artifact
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: None,
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert ctx.severity is not None
        assert ctx.severity == self.DOC


# ---------------------------------------------------------------------------
# CollectContextStage extras
# ---------------------------------------------------------------------------


class TestCollectContextExtras:
    def test_skips_when_no_plan(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=SimpleNamespace(repo_dir=artifacts.dir))
        result = CollectContextStage()(ctx)
        # should_run returns False when ctx.plan is falsy → SKIPPED.
        assert result.status == StageStatus.SKIPPED
        assert result.details == {}

    def test_skips_when_state_is_none(self, cfg, artifacts):
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=None)
        ctx.plan = {"files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details["skipped"] is True

    def test_rejects_unsafe_path(self, cfg, artifacts, monkeypatch):
        # Path traversal attempt must be silently dropped.
        (artifacts.dir / "ok.py").write_text("print('hi')", encoding="utf-8")
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [{"path": "../secret", "reason": "bad"}],
                "tests_to_inspect": ["ok.py"],
                "searches_to_run": [],
            },
        )
        state = SimpleNamespace(repo_dir=artifacts.dir, files=["ok.py"], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.collected)
        assert doc["files"] == []
        assert len(doc["tests"]) == 1  # tests_to_inspect is not safety-checked

    def test_rejects_nonexistent_path(self, cfg, artifacts):
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [{"path": "missing.py", "reason": "r"}],
                "tests_to_inspect": [],
                "searches_to_run": [],
            },
        )
        state = SimpleNamespace(repo_dir=artifacts.dir, files=[], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.collected)
        assert doc["files"] == []

    def test_search_failure_is_handled(self, cfg, artifacts, monkeypatch):
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [],
                "tests_to_inspect": [],
                "searches_to_run": [{"query": "x", "reason": "r"}],
            },
        )
        state = SimpleNamespace(repo_dir=artifacts.dir, files=[], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.collect_context.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"", b""),
        )
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.collected)
        assert doc["searches"] == [{"query": "x", "reason": "r", "matches": ""}]

    def test_searches_runs_rg(self, cfg, artifacts, monkeypatch):
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [],
                "tests_to_inspect": [],
                "searches_to_run": [{"query": "foo", "reason": "r"}],
            },
        )
        state = SimpleNamespace(repo_dir=artifacts.dir, files=[], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)

        captured = {}

        def fake_run(cmd, **kwargs):
            captured["cmd"] = cmd
            return subprocess.CompletedProcess(cmd, 0, b"file:1:foo\n", b"")

        monkeypatch.setattr(
            "reviewforge.pipeline.stages.collect_context.subprocess.run",
            fake_run,
        )
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        assert captured["cmd"][0] == "rg"
        doc = builder.read_json(artifacts.collected)
        assert doc["searches"][0]["matches"] == "file:1:foo"

    def test_searches_skips_invalid_items(self, cfg, artifacts, monkeypatch):
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": ["not a dict"],  # invalid type
                "tests_to_inspect": [],
                "searches_to_run": [{"reason": "no query"}, "not a dict"],
            },
        )
        state = SimpleNamespace(repo_dir=artifacts.dir, files=[], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.collect_context.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"", b""),
        )
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.collected)
        assert doc["files"] == []
        assert doc["searches"] == []  # all invalid

    def test_collect_context_uses_configured_worker_limit(self, cfg, artifacts, monkeypatch):
        cfg = replace(cfg, collect_context_workers=2)
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [{"path": "ok.py", "reason": "r"}],
                "tests_to_inspect": ["ok.py"],
                "searches_to_run": [{"query": "foo", "reason": "r"}],
            },
        )
        (artifacts.dir / "ok.py").write_text("print('ok')\n", encoding="utf-8")
        state = SimpleNamespace(repo_dir=artifacts.dir, files=["ok.py"], range_spec="x..y")
        ctx = _stage_context(cfg, artifacts, MagicMock(), state=state)
        ctx.plan = builder.read_json(artifacts.plan)
        seen = {}

        class DummyPool:
            def __init__(self, max_workers):
                seen["max_workers"] = max_workers
            def __enter__(self):
                return self
            def __exit__(self, exc_type, exc, tb):
                return False
            def submit(self, fn, *args, **kwargs):
                class F:
                    def result(self_nonlocal):
                        return fn(*args, **kwargs)
                return F()

        monkeypatch.setattr("reviewforge.pipeline.stages.collect_context.ThreadPoolExecutor", DummyPool)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.collect_context.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"", b""),
        )
        CollectContextStage()(ctx)
        assert seen["max_workers"] == 2

    def test_cache_key_changes_with_head_sha(self):
        base = ["review_diff", "p.md", "diff", "files", {}, {}, [], [], [], False, 100, 100]
        k1 = cache_key(base + ["sha1"])
        k2 = cache_key(base + ["sha2"])
        assert k1 != k2


# ---------------------------------------------------------------------------
# VerifyFindingsStage batched-run regression (raw/ directory)
# ---------------------------------------------------------------------------


class TestVerifyFindingsBatchedRegression:
    """Regression for the batched verification path.

    When ``candidate.findings`` has 2+ entries, ``VerifyFindingsStage``
    fans out one Pi call per finding and writes the per-finding JSON
    under ``raw/verify-N.json``. ``PiRunner.run_json`` writes with
    :meth:`Path.write_bytes`, which does NOT create parent directories.
    The artifacts manager must therefore create ``raw/`` eagerly, and the
    stage must defend itself if a caller hand-builds the run tree.
    """

    @staticmethod
    def _make_raw_pi(payloads):
        """Mock :class:`PiRunner` whose ``run_json`` mirrors the real
        implementation: it writes raw bytes via :meth:`Path.write_bytes`
        and does NOT create parent directories. Each call emits the next
        payload in ``payloads`` (last entry repeats when exhausted).
        """
        pi = MagicMock()

        def record(prompt, stdin, out, stage):
            idx = min(len(payloads) - 1, pi.run_json.call_count - 1)
            out.write_bytes(
                json.dumps(payloads[idx], ensure_ascii=False, indent=2).encode()
            )

        pi.run_json.side_effect = record
        return pi

    def _build_ctx(self, cfg, artifacts, pi):
        builder.write_json(artifacts.candidate, {
            "summary": "found 3",
            "findings": [
                {"severity": "major", "title": f"T{i}", "message": f"m{i}"}
                for i in range(3)
            ],
        })
        state = SimpleNamespace(
            diff_text="d",
            target_branch="m",
            source_branch="f",
            target_commit="t",
            source_commit="s",
            base_commit="b",
        )
        return _stage_context(cfg, artifacts, pi, state=state)

    def test_manager_create_materialises_raw_dir(self, cfg, artifacts):
        # Systemic guard: ``artifacts.manager.create`` must create
        # ``raw/`` so downstream ``Path.write_bytes`` calls succeed.
        if artifacts.raw_dir.exists():
            shutil.rmtree(artifacts.raw_dir)
        manager.create(cfg)
        assert artifacts.raw_dir.is_dir()

    def test_batched_run_writes_per_finding_outputs(self, cfg, artifacts):
        # Happy path: per-finding Pi outputs land in ``raw/`` and the
        # stage merges them into ``verified-findings.json``.
        cfg = replace(cfg, verify_findings=True)
        per_call = [
            {"summary": f"verify-{i}", "findings": [
                {"severity": "major", "title": f"V{i}", "message": f"M{i}"},
            ]}
            for i in range(1, 4)
        ]
        ctx = self._build_ctx(cfg, artifacts, self._make_raw_pi(per_call))
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 3, "batched": True}
        for i in range(1, 4):
            assert (artifacts.raw_dir / f"verify-{i}.json").is_file()
        merged = builder.read_json(artifacts.verified)
        # Order is non-deterministic (ThreadPoolExecutor + as_completed).
        assert {f["title"] for f in merged["findings"]} == {"V1", "V2", "V3"}

    def test_batched_validation_logs_invalid_finding(self, cfg, artifacts, capsys):
        cfg = replace(cfg, verify_findings=True)
        ctx = self._build_ctx(
            cfg,
            artifacts,
            self._make_raw_pi([
                {
                    "summary": "bad",
                    "findings": [{"severity": "major", "title": "bad", "message": ""}],
                }
            ]),
        )

        result = VerifyFindingsStage()(ctx)

        stderr = capsys.readouterr().err
        assert "merged verification output failed validation" in stderr
        assert '"message": ""' in stderr

    def test_batched_run_creates_raw_dir_when_missing(self, cfg, artifacts):
        # Defence-in-depth: even if the artifacts tree is missing
        # ``raw/`` (hand-built run dir, deleted before re-run), the
        # stage must materialise the directory before parallel work.
        cfg = replace(cfg, verify_findings=True)
        shutil.rmtree(artifacts.raw_dir)
        assert not artifacts.raw_dir.exists()
        ctx = self._build_ctx(cfg, artifacts, self._make_raw_pi([
            {"summary": f"verify-{i}", "findings": []} for i in range(1, 4)
        ]))
        result = VerifyFindingsStage()(ctx)
        assert result.status == StageStatus.OK
        assert result.details == {"findings": 0, "batched": True}
        assert artifacts.raw_dir.is_dir()
