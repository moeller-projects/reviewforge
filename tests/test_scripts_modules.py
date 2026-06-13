"""Focused unit tests for scripts/ modular review runner."""
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))


def load(name: str, path: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


from config import Config, env, is_true, require_uint  # noqa: E402
from infrastructure.ado import client as ado_client  # noqa: E402
from infrastructure.artifacts import builder, manager  # noqa: E402
from infrastructure.git import chunker  # noqa: E402
from infrastructure.pi import prompts  # noqa: E402
from infrastructure.pi.runner import PiRunner, strip_json_fences  # noqa: E402
from pipeline import orchestrator  # noqa: E402
from pipeline.stages import context_collect, context_digest, context_plan, findings, intent, severity, verify  # noqa: E402
from pipeline.validation import validate_review_doc, validate_stage  # noqa: E402

main_mod = load("main_mod", "scripts/main.py")
review_shim = load("review_shim", "scripts/review.py")
git_ops = load("git_ops", "scripts/infrastructure/git/ops.py")


def make_cfg(tmp_path: Path, **overrides) -> Config:
    files = {}
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        path = tmp_path / f"{name}.md"
        path.write_text(f"{name} prompt", encoding="utf-8")
        files[name] = path
    cfg = Config(
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
    return replace(cfg, **overrides)


class TestConfig:
    @pytest.mark.parametrize("value", ["1", "true", "TRUE", "yes", "on"])
    def test_is_true_accepts_truthy_values(self, value):
        assert is_true(value)

    @pytest.mark.parametrize("value", [None, "", "0", "false", "off", "no"])
    def test_is_true_rejects_falsy_values(self, value):
        assert not is_true(value)

    def test_require_uint_parses_non_negative_integer(self):
        assert require_uint("LIMIT", "123") == 123

    def test_require_uint_rejects_bad_value(self):
        with pytest.raises(SystemExit):
            require_uint("LIMIT", "abc")

    def test_env_uses_default_for_missing(self, monkeypatch):
        monkeypatch.delenv("MISSING_ENV", raising=False)
        assert env("MISSING_ENV", "fallback") == "fallback"

    def test_env_requires_value_without_default(self, monkeypatch):
        monkeypatch.delenv("MISSING_ENV", raising=False)
        with pytest.raises(SystemExit):
            env("MISSING_ENV")

    def test_from_env_parses_pr_url_and_defaults(self, tmp_path, monkeypatch):
        for key in ["ADO_ORG", "ADO_PROJECT", "ADO_REPO_ID", "PR_ID"]:
            monkeypatch.delenv(key, raising=False)
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("PR_URL", "https://dev.azure.com/org/Proj/_git/repo/pullrequest/7")
        monkeypatch.setenv("WORKSPACE", str(tmp_path))
        cfg = Config.from_env()
        assert (cfg.ado_org, cfg.ado_project, cfg.ado_repo_id, cfg.pr_id) == ("org", "Proj", "repo", "7")
        assert cfg.max_diff_bytes == 200000
        assert cfg.review_artifact_root == tmp_path / "artifacts"

    def test_validate_files_rejects_missing_prompt(self, tmp_path):
        cfg = make_cfg(tmp_path, review_prompt_path=tmp_path / "missing.md")
        with pytest.raises(SystemExit):
            cfg.validate_files()


class TestAdoClient:
    def test_parse_pr_url_rejects_unknown_format(self):
        with pytest.raises(SystemExit):
            ado_client.parse_pr_url("https://example.com/pr/1")

    def test_resolve_branches_uses_config_values(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, source_branch="refs/heads/feature/x", target_branch="refs/heads/main")
        monkeypatch.setattr(ado_client, "get_pr", MagicMock())
        assert ado_client.resolve_branches(cfg) == ("feature/x", "main")
        ado_client.get_pr.assert_not_called()

    def test_resolve_branches_fetches_missing_values(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, source_branch="", target_branch="")
        monkeypatch.setattr(
            ado_client,
            "get_pr",
            lambda _: {"sourceRefName": "refs/heads/feature/x", "targetRefName": "refs/heads/main"},
        )
        assert ado_client.resolve_branches(cfg) == ("feature/x", "main")

    def test_call_helper_builds_fetch_context_command(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        calls = []

        def fake_run(args, stdout, stderr):
            calls.append(args)
            return subprocess.CompletedProcess(args, 0, b"", b"")

        monkeypatch.setattr(ado_client.subprocess, "run", fake_run)
        ado_client.call_helper(cfg, "fetch-context", tmp_path)
        assert calls[0][1].endswith("ado_review.py")
        assert calls[0][2] == "fetch-context"
        assert calls[0][-2:] == ["--out", str(tmp_path)]

    def test_call_helper_raises_on_failure(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        monkeypatch.setattr(
            ado_client.subprocess,
            "run",
            lambda *a, **k: subprocess.CompletedProcess(a, 2, b"", b"boom"),
        )
        with pytest.raises(SystemExit):
            ado_client.call_helper(cfg, "fetch-context", tmp_path)


class TestArtifacts:
    def test_create_run_scoped_artifacts_and_latest_pointer(self, tmp_path):
        cfg = make_cfg(tmp_path, review_artifact_root=tmp_path / "artifacts", review_run_id="stable")
        artifacts = manager.create(cfg)
        assert artifacts.dir == tmp_path / "artifacts" / "pr-42" / "runs" / "stable"
        assert (tmp_path / "artifacts" / "pr-42" / "latest.txt").read_text().strip() == str(artifacts.dir)
        assert (artifacts.dir / "run-id.txt").read_text().strip() == "stable"

    def test_create_custom_artifact_dir_does_not_write_latest(self, tmp_path):
        custom = tmp_path / "custom"
        cfg = make_cfg(tmp_path, review_artifact_dir=str(custom))
        artifacts = manager.create(cfg)
        assert artifacts.dir == custom
        assert not (custom.parent / "pr-42" / "latest.txt").exists()
        assert (custom / "run-id.txt").read_text().strip() == "custom"

    def test_read_write_json_round_trips(self, tmp_path):
        path = tmp_path / "nested" / "data.json"
        builder.write_json(path, {"x": [1]})
        assert builder.read_json(path) == {"x": [1]}

    def test_changed_files_marks_known_languages_and_tests(self):
        assert builder.changed_files(["src/a.cs", "spec/foo_spec.rb", "Makefile"])[0] == {
            "file": "src/a.cs",
            "language": "C#",
            "isTest": False,
        }
        assert builder.changed_files(["spec/foo_spec.rb"])[0]["isTest"]
        assert builder.changed_files(["Makefile"])[0]["language"] == "Other"


class TestGitChunker:
    def state(self, tmp_path: Path, files: list[str]):
        return SimpleNamespace(repo_dir=tmp_path, files=files, range_spec="base..head")

    def test_build_chunks_groups_small_files(self, tmp_path, monkeypatch):
        diffs = {"a.py": "aaa", "b.py": "bbb"}
        monkeypatch.setattr(chunker, "run_git", lambda _repo, *_args: diffs[_args[-1]])
        chunks, truncated = chunker.build_chunks(self.state(tmp_path, ["a.py", "b.py"]), 10)
        assert not truncated
        assert len(chunks) == 1
        assert chunks[0].files_text == "a.py\nb.py\n"

    def test_build_chunks_splits_when_limit_exceeded(self, tmp_path, monkeypatch):
        diffs = {"a.py": "aaaaaa", "b.py": "bbbbbb"}
        monkeypatch.setattr(chunker, "run_git", lambda _repo, *_args: diffs[_args[-1]])
        chunks, _ = chunker.build_chunks(self.state(tmp_path, ["a.py", "b.py"]), 10)
        assert [c.files_text for c in chunks] == ["a.py\n", "b.py\n"]

    def test_build_chunks_truncates_oversized_file(self, tmp_path, monkeypatch):
        monkeypatch.setattr(chunker, "run_git", lambda *_args: "x" * 50)
        chunks, truncated = chunker.build_chunks(self.state(tmp_path, ["a.py"]), 10)
        assert truncated
        assert chunks[0].truncated
        assert "FILE DIFF TRUNCATED" in chunks[0].diff_text


class TestPrompts:
    def test_system_prompt_includes_language_and_standards(self, tmp_path):
        cfg = make_cfg(tmp_path, review_language="German")
        assert "LANGUAGE" in prompts.system_prompt(cfg)
        assert "German" in prompts.system_prompt(cfg)
        assert "standards prompt" in prompts.system_prompt(cfg)

    def test_stage_instruction_includes_available_context_files(self, tmp_path):
        cfg = make_cfg(tmp_path)
        metadata = tmp_path / "metadata.json"
        metadata.write_text('{"title":"PR"}', encoding="utf-8")
        intent_file = tmp_path / "intent.json"
        intent_file.write_text("intent", encoding="utf-8")
        paths = {
            "intent": intent_file,
            "plan": tmp_path / "missing-plan.json",
            "collected": tmp_path / "missing-collected.json",
            "digest": tmp_path / "missing-digest.json",
            "candidate": tmp_path / "missing-candidate.json",
            "verified": tmp_path / "missing-verified.json",
        }
        text = prompts.stage_instruction("intent", cfg, metadata, "a.py\n", [], [], paths)
        assert "Repository/project metadata" in text
        assert "Intent reconstruction" in text
        assert "Unified diff follows" in text

    def test_review_instruction_includes_chunk_and_truncation_notes(self, tmp_path):
        cfg = make_cfg(tmp_path)
        state = SimpleNamespace(
            target_branch="main",
            source_branch="feature",
            target_commit="t",
            source_commit="s",
            base_commit="b",
        )
        intent_file = tmp_path / "intent.json"
        digest_file = tmp_path / "digest.json"
        intent_file.write_text("intent", encoding="utf-8")
        digest_file.write_text("digest", encoding="utf-8")
        text = prompts.review_instruction(
            cfg,
            "a.py\n",
            state,
            [{"id": 1, "type": "Bug", "title": "Fix", "state": "Active", "description": "D", "acceptanceCriteria": "A"}],
            [{"workItemId": 1, "comments": [{"author": "Ann", "text": "note"}]}],
            [{"author": "Bob", "filePath": "a.py", "line": 5, "firstComment": "existing"}],
            intent_file,
            digest_file,
            "chunk 1/2",
            True,
        )
        assert "LARGE DIFF CHUNK" in text
        assert "Work Item #1" in text
        assert "EXISTING PR COMMENTS" in text
        assert "diff was truncated" in text


class TestPiRunner:
    def test_strip_json_fences(self, tmp_path):
        path = tmp_path / "out.txt"
        path.write_text("```json\n{\"ok\": true}\n```\n", encoding="utf-8")
        strip_json_fences(path)
        assert json.loads(path.read_text()) == {"ok": True}

    def test_run_json_writes_valid_output_and_removes_ado_env(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        seen_env = {}

        def fake_run(cmd, input, stdout, stderr, timeout, env):
            seen_env.update(env)
            return subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"warn\n")

        monkeypatch.setenv("ADO_AUTH_TOKEN", "secret")
        monkeypatch.setattr("infrastructure.pi.runner.subprocess.run", fake_run)
        output = tmp_path / "pi.json"
        PiRunner(cfg).run_json(tmp_path / "prompt.md", "stdin", output, "stage")
        assert json.loads(output.read_text()) == {"ok": True}
        assert "ADO_AUTH_TOKEN" not in seen_env

    def test_run_json_repairs_invalid_json(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        calls = []

        def fake_run(cmd, input, stdout, stderr, timeout, env):
            calls.append(cmd)
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            return subprocess.CompletedProcess(cmd, 0, b'{"repaired": true}', b"")

        monkeypatch.setattr("infrastructure.pi.runner.subprocess.run", fake_run)
        output = tmp_path / "pi.json"
        PiRunner(cfg).run_json(tmp_path / "prompt.md", "stdin", output, "stage")
        assert json.loads(output.read_text()) == {"repaired": True}
        assert len(calls) == 2

    def test_run_json_raises_on_nonzero(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        monkeypatch.setattr(
            "infrastructure.pi.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess([], 9, b"", b"bad"),
        )
        with pytest.raises(SystemExit):
            PiRunner(cfg).run_json(tmp_path / "prompt.md", "stdin", tmp_path / "out.json", "stage")


class TestValidation:
    def test_validate_review_doc_rejects_bad_severity(self):
        with pytest.raises(SystemExit):
            validate_review_doc({"summary": "x", "findings": [{"severity": "critical", "title": "T", "message": "M"}]})

    def test_validate_stage_rejects_missing_intent_fields(self):
        with pytest.raises(SystemExit):
            validate_stage({"pr_intent": "x"}, "intent reconstruction")

    def test_validate_stage_accepts_context_plan(self):
        validate_stage({"files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}, "context planning")


class TestStages:
    def make_ctx(self, tmp_path: Path):
        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        artifacts.metadata.write_text("{}", encoding="utf-8")
        state = SimpleNamespace(
            diff_text="diff",
            repo_dir=tmp_path,
            files=["a.py"],
            range_spec="base..head",
            target_branch="main",
            source_branch="feature",
            target_commit="t",
            source_commit="s",
            base_commit="b",
        )
        pi = MagicMock()
        ctx = orchestrator.ReviewContext(
            state=state,
            artifacts=artifacts,
            pi=pi,
            files_text="a.py\n",
            wi_context=[],
            wi_comments_context=[],
            thread_context=[],
            system_prompt="system",
            artifact_tmp=tmp_path / ".tmp",
        )
        ctx.artifact_tmp.mkdir(exist_ok=True)
        return cfg, ctx

    def test_intent_stage_invokes_pi_with_intent_prompt(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        intent.run(cfg, ctx)
        ctx.pi.run_json.assert_called_once()
        assert ctx.pi.run_json.call_args.args[0] == cfg.intent_prompt_path
        assert ctx.pi.run_json.call_args.args[2] == ctx.artifacts.intent

    def test_context_plan_stage_invokes_pi_with_plan_prompt(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        context_plan.run(cfg, ctx)
        assert ctx.pi.run_json.call_args.args[0] == cfg.context_plan_prompt_path

    def test_context_digest_stage_invokes_pi_with_digest_prompt(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        context_digest.run(cfg, ctx)
        assert ctx.pi.run_json.call_args.args[0] == cfg.context_digest_prompt_path

    def test_verify_stage_copies_candidate_when_disabled(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        cfg = replace(cfg, verify_findings=False)
        ctx.artifacts.candidate.write_text('{"summary":"x","findings":[]}', encoding="utf-8")
        verify.run(cfg, ctx)
        assert json.loads(ctx.artifacts.verified.read_text()) == {"summary": "x", "findings": []}
        ctx.pi.run_json.assert_not_called()

    def test_severity_stage_invokes_pi_with_severity_prompt(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        severity.run(cfg, ctx)
        assert ctx.pi.run_json.call_args.args[0] == cfg.severity_prompt_path

    def test_findings_single_pass_invokes_pi_and_writes_system_prompt(self, tmp_path):
        cfg, ctx = self.make_ctx(tmp_path)
        findings.run(cfg, ctx)
        ctx.pi.run_json.assert_called_once()
        assert ctx.artifacts.system_prompt.read_text() == "system"

    def test_findings_chunked_deduplicates_results(self, tmp_path, monkeypatch):
        cfg, ctx = self.make_ctx(tmp_path)
        cfg = replace(cfg, chunk_trigger_diff_bytes=1, max_diff_bytes=5)
        ctx.state.diff_text = "x" * 20

        class FakeChunk:
            def __init__(self, diff_text, files_text, truncated=False):
                self.diff_text = diff_text
                self.files_text = files_text
                self.truncated = truncated

        monkeypatch.setattr(
            findings,
            "build_chunks",
            lambda _state, _max: ([FakeChunk("d1", "a.py\n"), FakeChunk("d2", "b.py\n")], False),
        )

        def fake_run_json(_prompt, _stdin, out, _stage):
            builder.write_json(out, {"summary": "s", "findings": [{"severity": "major", "title": "T", "message": "M", "file": "a.py", "line": 1}]})

        ctx.pi.run_json.side_effect = fake_run_json
        findings.run(cfg, ctx)
        doc = builder.read_json(ctx.artifacts.candidate)
        assert len(doc["findings"]) == 1
        assert "2 diff chunk" in doc["summary"]

    def test_context_collect_reads_safe_files_and_searches(self, tmp_path, monkeypatch):
        cfg, ctx = self.make_ctx(tmp_path)
        (tmp_path / "a.py").write_text("print('hello')\n", encoding="utf-8")
        builder.write_json(
            ctx.artifacts.plan,
            {
                "files_to_read": [{"path": "a.py", "reason": "changed"}, {"path": "../secret", "reason": "bad"}],
                "tests_to_inspect": ["a.py"],
                "searches_to_run": [{"query": "hello", "reason": "callsite"}],
            },
        )
        monkeypatch.setattr(
            context_collect.subprocess,
            "run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"a.py:1:hello\n", b""),
        )
        context_collect.run(cfg, ctx)
        doc = builder.read_json(ctx.artifacts.collected)
        assert doc["files"][0]["path"] == "a.py"
        assert len(doc["files"]) == 1
        assert doc["tests"][0]["path"] == "a.py"
        assert "hello" in doc["searches"][0]["matches"]


class TestOrchestratorHelpers:
    def test_should_skip_draft_unless_forced(self, tmp_path):
        cfg = make_cfg(tmp_path)
        assert orchestrator.should_skip(cfg, {"isDraft": True})["summary"] == "Skipped: PR is a draft."
        assert orchestrator.should_skip(replace(cfg, force_review=True), {"isDraft": True}) is None

    def test_should_skip_disallowed_target_branch(self, tmp_path):
        cfg = make_cfg(tmp_path, review_target_branches="main,release")
        skipped = orchestrator.should_skip(cfg, {"status": "active", "targetRefName": "refs/heads/dev"})
        assert "not in the review policy" in skipped["summary"]

    def test_ensure_tools_raises_when_tool_missing(self, monkeypatch):
        monkeypatch.setattr(orchestrator.shutil, "which", lambda tool: None if tool == "pi" else f"/bin/{tool}")
        with pytest.raises(SystemExit):
            orchestrator.ensure_tools()


class TestTopLevelEntryPoints:
    def test_main_and_review_shim_import_and_run(self, monkeypatch, tmp_path):
        cfg = make_cfg(tmp_path)
        monkeypatch.setattr(main_mod.Config, "from_env", classmethod(lambda cls: cfg))
        monkeypatch.setattr(main_mod.Config, "validate_files", lambda self: None)
        monkeypatch.setattr(main_mod, "run", lambda _cfg: 17)
        assert main_mod.main() == 17
        assert hasattr(review_shim, "main")


class TestGitOps:
    def test_run_git_returns_stdout(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            git_ops.subprocess,
            "run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"hello\n", b""),
        )
        assert git_ops.run_git(tmp_path, "status").strip() == "hello"

    def test_run_logged_prints_and_raises_on_failure(self, tmp_path, monkeypatch):
        def fake_run(*a, **k):
            return subprocess.CompletedProcess(a, 1, b"out\n", b"err\n")

        monkeypatch.setattr(git_ops.subprocess, "run", fake_run)
        with pytest.raises(SystemExit):
            git_ops.run_logged("step", ["git", "status"], tmp_path)

    def test_prepare_repo_and_cleanup(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, clone_root=tmp_path / "clones")

        def fake_run(cmd, cwd=None, stdout=None, stderr=None, env=None):
            if cmd[:2] == ["git", "merge-base"]:
                return subprocess.CompletedProcess(cmd, 0, b"base123\n", b"")
            if cmd[:2] == ["git", "rev-parse"]:
                if "target" in cmd[-1]:
                    return subprocess.CompletedProcess(cmd, 0, b"target123\n", b"")
                return subprocess.CompletedProcess(cmd, 0, b"source123\n", b"")
            if cmd[:2] == ["git", "diff"] and "--name-only" in cmd:
                return subprocess.CompletedProcess(cmd, 0, b"src/a.py\n", b"")
            if cmd[:2] == ["git", "diff"]:
                return subprocess.CompletedProcess(cmd, 0, b"difftext", b"")
            return subprocess.CompletedProcess(cmd, 0, b"", b"")

        monkeypatch.setattr(git_ops.subprocess, "run", fake_run)
        state = git_ops.prepare_repo(cfg, "feature/x", "main")
        assert state.base_commit == "base123"
        assert state.files == ["src/a.py"]
        git_ops.cleanup(state)


class TestOrchestratorRun:
    def test_run_happy_path_dry_run(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, dry_run=True)
        artifacts = manager.create(cfg)
        state = SimpleNamespace(
            repo_dir=tmp_path,
            source_branch="feature/x",
            target_branch="main",
            base_commit="base123",
            source_commit="source123",
            target_commit="target123",
            diff_text="difftext",
            files=["src/a.py"],
            range_spec="base123..source123",
        )

        monkeypatch.setattr(orchestrator, "ensure_tools", lambda: None)
        monkeypatch.setattr(orchestrator.ado, "resolve_branches", lambda _cfg: ("feature/x", "main"))
        monkeypatch.setattr(orchestrator.manager, "create", lambda _cfg: artifacts)
        monkeypatch.setattr(orchestrator.ops, "prepare_repo", lambda _cfg, _src, _tgt: state)
        monkeypatch.setattr(orchestrator.ops, "cleanup", lambda _state: None)
        monkeypatch.setattr(orchestrator.ops, "run_git", lambda *_a, **_k: "commit1\n")

        def fake_call_helper(_cfg, command, artifact_dir, findings=None):
            if command == "fetch-context":
                builder.write_json(artifact_dir / "metadata.json", {"status": "active", "isDraft": False, "targetRefName": "refs/heads/main"})
                builder.write_json(artifact_dir / "work-items.json", [])
                builder.write_json(artifact_dir / "work-item-comments.json", [])
                builder.write_json(artifact_dir / "threads.json", [])
            else:
                builder.write_json(artifact_dir / "posted-findings.json", {"ok": True})

        monkeypatch.setattr(orchestrator.ado, "call_helper", fake_call_helper)
        monkeypatch.setattr(orchestrator.intent, "run", lambda _cfg, _ctx: builder.write_json(artifacts.intent, {"pr_intent": "x", "changed_behaviors": [], "risk_areas": []}))
        monkeypatch.setattr(orchestrator.context_plan, "run", lambda _cfg, _ctx: builder.write_json(artifacts.plan, {"files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}))
        monkeypatch.setattr(orchestrator.context_collect, "run", lambda _cfg, _ctx: builder.write_json(artifacts.collected, {"files": [], "tests": [], "searches": []}))
        monkeypatch.setattr(orchestrator.context_digest, "run", lambda _cfg, _ctx: builder.write_json(artifacts.digest, {"relevant_context": [], "possible_intentional_choices": [], "context_gaps": []}))
        monkeypatch.setattr(orchestrator.findings, "run", lambda _cfg, _ctx: builder.write_json(artifacts.candidate, {"summary": "s", "findings": []}))
        monkeypatch.setattr(orchestrator.verify, "run", lambda _cfg, _ctx: builder.write_json(artifacts.verified, {"summary": "s", "findings": []}))
        monkeypatch.setattr(orchestrator.severity, "run", lambda _cfg, _ctx: builder.write_json(artifacts.severity, {"summary": "s", "findings": []}))

        assert orchestrator.run(cfg) == 0
        assert builder.read_json(artifacts.final)["findings"] == []

    def test_run_skips_nonactive_pr(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        monkeypatch.setattr(orchestrator, "ensure_tools", lambda: None)
        monkeypatch.setattr(orchestrator.ado, "resolve_branches", lambda _cfg: ("feature/x", "main"))
        monkeypatch.setattr(orchestrator.manager, "create", lambda _cfg: artifacts)
        monkeypatch.setattr(orchestrator.ops, "prepare_repo", lambda _cfg, _src, _tgt: SimpleNamespace(repo_dir=tmp_path, files=[], diff_text="", base_commit="b", source_commit="s", target_commit="t", range_spec="b..s", source_branch="feature/x", target_branch="main"))
        monkeypatch.setattr(orchestrator.ops, "cleanup", lambda _state: None)
        monkeypatch.setattr(orchestrator.ado, "call_helper", lambda *_a, **_k: builder.write_json(artifacts.metadata, {"status": "closed", "isDraft": False, "targetRefName": "refs/heads/main"}))
        assert orchestrator.run(cfg) == 0
