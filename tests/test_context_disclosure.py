from __future__ import annotations

import json
import subprocess
from pathlib import Path
from types import SimpleNamespace

from reviewforge.ai.runner import PiRunner, _parse_context_file_reads
from conftest import FakePopen
from reviewforge.config import Config
from reviewforge.artifacts import manager
from reviewforge.pipeline.stage import StageContext
from reviewforge.pipeline.context_staging import stage_context_files
from reviewforge.pipeline.crg.prompt import build_wave2_section
from reviewforge.reasoning.single_pi import (
    _byte_cap_with_pointer,
    render_section,
)


def _cfg(tmp_path: Path) -> Config:
    prompts = {}
    for name in ("review", "intent", "plan", "digest", "verify", "severity", "standards"):
        prompts[name] = tmp_path / f"{name}.md"
        prompts[name].write_text("prompt", encoding="utf-8")
    return Config(
        ado_org="org",
        ado_project="project",
        ado_repo_id="repo",
        pr_id="1",
        ado_token="cli-secret",
        source_branch="source",
        target_branch="target",
        workspace=tmp_path,
        clone_root=tmp_path,
        review_language="English",
        review_prompt_path=prompts["review"],
        intent_prompt_path=prompts["intent"],
        context_plan_prompt_path=prompts["plan"],
        context_digest_prompt_path=prompts["digest"],
        verify_prompt_path=prompts["verify"],
        severity_prompt_path=prompts["severity"],
        standards_path=prompts["standards"],
        pi_model="model",
        max_diff_bytes=1000,
        chunk_trigger_diff_bytes=1000,
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
        review_run_id="run",
    )


def _context(tmp_path: Path) -> SimpleNamespace:
    artifact_dir = tmp_path / "artifacts"
    artifact_dir.mkdir()
    paths = {}
    for name in ("metadata", "commits", "changed_files", "work_items", "threads", "graph_context"):
        paths[name] = artifact_dir / f"{name}.{'txt' if name == 'commits' else 'json'}"
    paths["metadata"].write_text(json.dumps({"token": "cli-secret", "project": "P"}), encoding="utf-8")
    paths["commits"].write_text("abc first\ndef second\n", encoding="utf-8")
    paths["changed_files"].write_text(json.dumps([{"file": "src/a.py"}]), encoding="utf-8")
    paths["work_items"].write_text(json.dumps([{"id": 1, "title": "item"}]), encoding="utf-8")
    paths["threads"].write_text(json.dumps([{"id": 2, "comments": []}]), encoding="utf-8")
    paths["graph_context"].write_text(json.dumps({"status": "ok", "impacted_files": ["src/a.py"]}), encoding="utf-8")
    return SimpleNamespace(
        cfg=SimpleNamespace(ado_token="cli-secret"),
        state=SimpleNamespace(
            repo_dir=tmp_path / "repo",
            files=["src/a.py"],
            diff_text="diff --git a/src/a.py b/src/a.py\n",
            cleanup_paths=[],
        ),
        artifacts=SimpleNamespace(**paths),
        extras={"review_context": {"mode": "full", "previousComments": []}},
    )


def test_staging_copies_complete_data_and_redacts_secrets(tmp_path, monkeypatch):
    ctx = _context(tmp_path)
    ctx.state.repo_dir.mkdir()
    monkeypatch.setenv("ADO_AUTH_TOKEN", "env-secret")
    staged = stage_context_files(ctx)

    assert staged == ctx.state.repo_dir / ".reviewforge-context"
    assert json.loads((staged / "metadata.json").read_text())["project"] == "P"
    assert "cli-secret" not in (staged / "metadata.json").read_text()
    assert "env-secret" not in (staged / "metadata.json").read_text()
    assert (staged / "commits.txt").read_text() == "abc first\ndef second\n"
    assert ".reviewforge-context" not in ctx.state.diff_text
    assert (staged / "changed-files.json").exists()
    assert staged in ctx.state.cleanup_paths
    index = json.loads((staged / "index.json").read_text())
    assert index["metadata.json"]["top_level_keys"] == ["project", "token"]
    assert ".reviewforge-context" not in json.dumps(ctx.state.files)


def test_staging_skips_without_checkout(tmp_path):
    ctx = _context(tmp_path)
    ctx.state.repo_dir = None
    assert stage_context_files(ctx) is None
    assert "context_staging_dir" not in ctx.extras
def test_skip_path_has_no_context_preamble_or_pointers(tmp_path):
    cfg = _cfg(tmp_path)
    artifacts = manager.create(cfg)
    state = SimpleNamespace(repo_dir=None, files=[], cleanup_paths=[])
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=SimpleNamespace())
    assert stage_context_files(ctx) is None
    from reviewforge.reasoning.single_pi import _build_single_pi_prefix
    assert ".reviewforge-context" not in _build_single_pi_prefix(ctx)


def test_staging_skips_preexisting_checkout_context(tmp_path):
    ctx = _context(tmp_path)
    ctx.state.repo_dir.mkdir()
    existing = ctx.state.repo_dir / ".reviewforge-context"
    existing.mkdir()
    keep = existing / "tracked.txt"
    keep.write_text("repository content", encoding="utf-8")

    assert stage_context_files(ctx) is None
    assert keep.read_text(encoding="utf-8") == "repository content"
    assert "context_staging_dir" not in ctx.extras


def test_staging_can_exclude_graph_context_until_enrichment(tmp_path):
    ctx = _context(tmp_path)
    ctx.state.repo_dir.mkdir()
    assert stage_context_files(ctx, include_graph_context=False)
    assert not (ctx.state.repo_dir / ".reviewforge-context" / "graph-context.json").exists()
    assert stage_context_files(ctx)
    assert (ctx.state.repo_dir / ".reviewforge-context" / "graph-context.json").exists()


def test_wave_two_pointers_use_nested_keys_and_survive_small_caps():
    context = {
        "api_surface": {
            "status": "ok",
            "breaking_candidates": [{"symbol": str(i)} for i in range(16)],
        }
    }
    rendered = build_wave2_section(context, 4096, Path("/repo"))
    assert "key: api_surface.breaking_candidates" in rendered
    capped = _byte_cap_with_pointer("0123456789", 8, ".reviewforge-context/graph-context.json")
    assert len(capped.encode("utf-8")) <= 8
    assert "full data" not in capped


def test_render_section_pointer_only_when_truncated():
    assert render_section("Items:", ["a", "b"], 2, ".reviewforge-context/x.json (key: items)") == "Items:\na\nb"
    assert "…and 1 more — full data: .reviewforge-context/x.json (key: items)" in render_section(
        "Items:", ["a", "b", "c"], 2, ".reviewforge-context/x.json (key: items)"
    )
    assert "more" not in render_section("Items:", ["a", "b", "c"], 2, None)


def test_runner_sets_review_cwd_and_counts_context_reads(tmp_path, monkeypatch):
    cfg = _cfg(tmp_path)
    calls: list[dict] = []

    def fake_popen(cmd, cwd=None, **kwargs):
        calls.append({"cwd": cwd})
        return FakePopen(
            cmd,
            returncode=0,
            stdout=b'{"ok": true}',
            stderr=b'read .reviewforge-context/metadata.json\nread .reviewforge-context/metadata.json\n',
        )

    monkeypatch.setattr("reviewforge.ai.runner.subprocess.Popen", fake_popen)
    runner = PiRunner(cfg)
    runner.set_working_dir(tmp_path)
    runner.run_json(cfg.review_prompt_path, "first", tmp_path / "one.json", "one")
    runner.run_json(cfg.review_prompt_path, "second", tmp_path / "two.json", "two")

    assert all(call["cwd"] == str(tmp_path) for call in calls)
    assert runner.context_file_reads == {".reviewforge-context/metadata.json": 4}
    assert _parse_context_file_reads("unparseable stderr") == "unknown"
    assert _parse_context_file_reads("") == "unknown"
