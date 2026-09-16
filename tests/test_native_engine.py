"""Tests for the native in-process reasoning engine (pydantic-ai)."""
from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
sys.path.insert(0, str(SRC))

from pydantic_ai.messages import ModelResponse, TextPart, ToolCallPart  # noqa: E402
from pydantic_ai.models.function import AgentInfo, FunctionModel  # noqa: E402

from reviewforge.artifacts import builder, manager  # noqa: E402
from reviewforge.config import Config  # noqa: E402
from reviewforge.exceptions import ReasoningEngineError  # noqa: E402
from reviewforge.pipeline.stage import StageContext  # noqa: E402
from reviewforge.reasoning.engine import get_engine  # noqa: E402
from reviewforge.reasoning.multi_stage import MultiStageReasoningEngine  # noqa: E402
from reviewforge.reasoning.native import (
    _NATIVE_DENIED_PATTERNS,
    CodexFileCredentialSource,
    NativeReasoningEngine,
    resolve_native_model,
)
from reviewforge.reasoning.native_tools import ReviewCollectorToolset  # noqa: E402
from reviewforge.reasoning.single_pi import SinglePiReasoningEngine  # noqa: E402

_READ_ONLY_FS_TOOLS = {"read_file", "list_directory", "search_files", "find_files", "file_info"}
_COLLECTOR_TOOLS = {"read_context", "record_finding", "record_uncertainty", "task_done"}
_FORBIDDEN_TOOL_NAMES = {"write_file", "edit_file", "create_directory", "shell", "run_command"}


def _cfg(tmp_path: Path) -> Config:
    standards = tmp_path / "standards.md"
    standards.write_text("standards", encoding="utf-8")
    native_prompt = tmp_path / "native-review.md"
    native_prompt.write_text("native prompt", encoding="utf-8")
    prompt = tmp_path / "prompt.md"
    prompt.write_text("prompt", encoding="utf-8")
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
        review_prompt_path=prompt,
        intent_prompt_path=prompt,
        context_plan_prompt_path=prompt,
        context_digest_prompt_path=prompt,
        verify_prompt_path=prompt,
        severity_prompt_path=prompt,
        standards_path=standards,
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
        native_review_prompt_path=native_prompt,
    )


def _stage_context(cfg: Config, repo_dir: Path) -> StageContext:
    artifacts = manager.create(cfg)
    builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
    state = SimpleNamespace(
        diff_text="diff --git a/a.py b/a.py\n+ x = 1\n",
        files=["a.py"],
        repo_dir=repo_dir,
        range_spec="main...feature",
    )
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=MagicMock())
    artifacts.commits.write_text("abc add x\n", encoding="utf-8")
    ctx.files_text = "a.py\n"
    return ctx


def _finding_args(title: str, **overrides: Any) -> dict[str, Any]:
    args: dict[str, Any] = {
        "title": title,
        "observation": "observed behavior",
        "impact": "why it matters",
        "recommendation": "concrete fix",
        "severity": "major",
        "confidence": "high",
        "file": "a.py",
        "line": 1,
        "evidence": {
            "changedLines": [1],
            "relatedFiles": ["a.py"],
            "whyNewInThisPr": "introduced by this diff",
            "whyNotIntentional": "contradicts the stated intent",
        },
    }
    args.update(overrides)
    return args


def _narrative_text(summary: str = "all good") -> str:
    return json.dumps(
        {
            "review_summary": {"summary": summary, "notes": "n"},
            "pr_summary": {"intent": "ship it", "work_type": "change"},
            "good_practices": [{"observation": "tidy diff", "files": ["a.py"]}],
        }
    )


def _tool_call(name: str, args: dict[str, Any], call_id: str) -> ToolCallPart:
    return ToolCallPart(name, args=args, tool_call_id=call_id)


def _run_engine(cfg: Config, ctx: StageContext, model: FunctionModel):
    engine = NativeReasoningEngine(cfg, model_override=model)
    return engine, engine.execute(ctx)


def _tool_returns(messages: list) -> list[str]:
    return [
        str(getattr(part, "content", ""))
        for message in messages
        for part in getattr(message, "parts", [])
        if part.__class__.__name__ == "ToolReturnPart"
    ]


class TestNativeEngineLoop:
    def test_happy_path(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()
        (repo / "a.py").write_text("x = 1\n", encoding="utf-8")
        seen: dict[str, Any] = {}
        calls = {"n": 0}

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            calls["n"] += 1
            seen["tools"] = sorted(t.name for t in info.function_tools)
            if calls["n"] == 1:
                return ModelResponse(parts=[
                    _tool_call("record_finding", _finding_args("First"), "c1"),
                    _tool_call("record_finding", _finding_args("Second", line=2), "c2"),
                ])
            if calls["n"] == 2:
                return ModelResponse(parts=[_tool_call("task_done", {"summary": "done"}, "c3")])
            return ModelResponse(parts=[TextPart(_narrative_text())])

        cfg = _cfg(tmp_path)
        engine, result = _run_engine(cfg, _stage_context(cfg, repo), FunctionModel(model_fn))

        assert [f.title for f in result.findings] == ["First", "Second"]
        assert result.review_summary.summary == "all good"
        assert result.pr_summary.intent == "ship it"
        assert len(result.good_practices) == 1
        assert result.metadata.model.reasoning_engine == "native"
        assert result.metadata.model.model == "openai-codex:gpt-5.6-luna"
        assert engine.collector is not None and engine.collector.finished_deliberately
        assert "without task_done" not in result.metrics.reviewDepth

    def test_invalid_finding_retried(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()
        calls = {"n": 0}
        seen: dict[str, Any] = {}

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            calls["n"] += 1
            seen.setdefault("returns", []).extend(_tool_returns(messages))
            if calls["n"] == 1:
                # regression=true without evidence.changedLines violates the
                # RichFinding cross-field constraint, not the tool signature.
                bad = _finding_args("Bad", regression=True, evidence={})
                return ModelResponse(parts=[_tool_call("record_finding", bad, "c1")])
            if calls["n"] == 2:
                return ModelResponse(parts=[
                    _tool_call("record_finding", _finding_args("Fixed"), "c2"),
                    _tool_call("task_done", {}, "c3"),
                ])
            return ModelResponse(parts=[TextPart(_narrative_text())])

        cfg = _cfg(tmp_path)
        engine = NativeReasoningEngine(cfg, model_override=FunctionModel(model_fn))
        result = engine.execute(_stage_context(cfg, repo))

        assert [f.title for f in result.findings] == ["Fixed"]
        assert any("NOT recorded" in content for content in seen["returns"])

    def test_missing_task_done_flagged(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()
        calls = {"n": 0}

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            calls["n"] += 1
            if calls["n"] == 1:
                return ModelResponse(parts=[_tool_call("record_finding", _finding_args("Only"), "c1")])
            return ModelResponse(parts=[TextPart(_narrative_text())])

        cfg = _cfg(tmp_path)
        engine = NativeReasoningEngine(cfg, model_override=FunctionModel(model_fn))
        result = engine.execute(_stage_context(cfg, repo))

        assert [f.title for f in result.findings] == ["Only"]
        assert engine.collector is not None and not engine.collector.finished_deliberately
        assert "without task_done" in result.metrics.reviewDepth

    def test_read_only_toolset(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()
        seen: dict[str, Any] = {}

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            seen["tools"] = {t.name for t in info.function_tools}
            return ModelResponse(parts=[TextPart(_narrative_text())])

        cfg = _cfg(tmp_path)
        engine = NativeReasoningEngine(cfg, model_override=FunctionModel(model_fn))
        engine.execute(_stage_context(cfg, repo))

        assert seen["tools"] == _READ_ONLY_FS_TOOLS | _COLLECTOR_TOOLS
        assert seen["tools"].isdisjoint(_FORBIDDEN_TOOL_NAMES)

    def test_secret_files_denied_from_reads(self, tmp_path):
        from pydantic_ai.exceptions import ModelRetry
        from pydantic_ai_harness.filesystem import FileSystem

        repo = tmp_path / "repo"
        repo.mkdir()
        (repo / ".env.production").write_text("TOKEN=secret\n", encoding="utf-8")
        (repo / ".envrc").write_text("export TOKEN=secret\n", encoding="utf-8")
        (repo / "server.pem").write_text("-----BEGIN PRIVATE KEY-----\n", encoding="utf-8")
        (repo / "id_rsa.key").write_text("PRIVATE\n", encoding="utf-8")
        (repo / "config").mkdir()
        (repo / "config" / "secrets.yml").write_text("password: x\n", encoding="utf-8")
        (repo / "app.py").write_text("x = 1\n", encoding="utf-8")

        toolset = FileSystem(
            root_dir=repo,
            denied_patterns=_NATIVE_DENIED_PATTERNS,
        ).get_toolset()

        for secret in (".env.production", ".envrc", "server.pem", "id_rsa.key", "config/secrets.yml"):
            with pytest.raises(ModelRetry):
                asyncio.run(toolset.read_file(secret))
        assert "x = 1" in asyncio.run(toolset.read_file("app.py"))

    def test_usage_mapped_to_metrics(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            if not any(
                part.__class__.__name__ == "ToolReturnPart"
                for message in messages
                for part in getattr(message, "parts", [])
            ):
                return ModelResponse(parts=[_tool_call("task_done", {}, "c1")])
            return ModelResponse(parts=[TextPart(_narrative_text())])

        cfg = _cfg(tmp_path)
        engine = NativeReasoningEngine(cfg, model_override=FunctionModel(model_fn))
        result = engine.execute(_stage_context(cfg, repo))

        assert result.metrics.invocationCount >= 2  # tool call round + final output
        assert result.metadata.tokens.input == result.metrics.piInputTokens
        assert result.metadata.tokens.output == result.metrics.piOutputTokens
        assert result.metadata.tokens.total == result.metrics.piTotalTokens
        assert result.metrics.changedFilesReviewed == 1
        assert result.metadata.duration_ms >= 0

    def test_crash_keeps_partial_findings(self, tmp_path):
        repo = tmp_path / "repo"
        repo.mkdir()
        calls = {"n": 0}

        def model_fn(messages: list, info: AgentInfo) -> ModelResponse:
            calls["n"] += 1
            if calls["n"] == 1:
                return ModelResponse(parts=[_tool_call("record_finding", _finding_args("Kept"), "c1")])
            raise RuntimeError("provider exploded")

        cfg = _cfg(tmp_path)
        engine = NativeReasoningEngine(cfg, model_override=FunctionModel(model_fn))
        with pytest.raises(ReasoningEngineError):
            engine.execute(_stage_context(cfg, repo))

        assert engine.collector is not None
        assert [f.title for f in engine.collector.findings] == ["Kept"]





class TestCollectorTools:
    def test_context_tool_reads_staging(self, tmp_path):
        staging = tmp_path / ".reviewforge-context"
        staging.mkdir()
        (staging / "crg-analysis.json").write_text('{"status": "ok"}', encoding="utf-8")
        collector = ReviewCollectorToolset(staging, "")

        assert collector._read_context("crg-analysis.json") == '{"status": "ok"}'
        assert "invalid context file name" in collector._read_context("../etc/passwd")
        assert "not present" in collector._read_context("missing.json")

    def test_context_tool_without_staging(self):
        collector = ReviewCollectorToolset(None, "")
        assert "no staged context" in collector._read_context("crg-analysis.json")

    def test_context_tool_read_error(self, tmp_path, monkeypatch):
        staging = tmp_path / ".reviewforge-context"
        staging.mkdir()
        (staging / "crg-analysis.json").write_text("{}", encoding="utf-8")

        def _boom(self, *args, **kwargs):
            raise OSError("boom")

        monkeypatch.setattr(Path, "read_text", _boom)
        collector = ReviewCollectorToolset(staging, "")
        assert "could not read context file" in collector._read_context("crg-analysis.json")

    def test_record_uncertainty_roundtrip_and_validation(self):
        collector = ReviewCollectorToolset(None, "")
        assert "recorded" in collector._record_uncertainty("topic", "why")
        assert collector.uncertainties[0].topic == "topic"
        # cross-chunk uncertainties must set confidence low.
        out = collector._record_uncertainty("cross-chunk: x", "y", "high")
        assert "NOT recorded" in out
        assert len(collector.uncertainties) == 1


class TestRegistration:
    def test_engine_registered(self):
        assert isinstance(get_engine("native"), NativeReasoningEngine)
        assert isinstance(get_engine("single_pi"), SinglePiReasoningEngine)
        assert isinstance(get_engine("multi_stage"), MultiStageReasoningEngine)


class TestModelResolution:
    def test_codex_model_builds_credential_backed_provider(self, tmp_path):
        from pydantic_ai.models.openai_codex import OpenAICodexModel
        from pydantic_ai.providers.openai_codex import OpenAICodexProvider

        cfg = _cfg(tmp_path)
        model = resolve_native_model(cfg)
        assert isinstance(model, OpenAICodexModel)
        provider = model._provider
        assert isinstance(provider, OpenAICodexProvider)
        assert provider.name == "openai-codex"

    def test_non_codex_model_passes_through(self, tmp_path):
        cfg = _cfg(tmp_path).with_overrides(native_model="openai:gpt-5.5")
        assert resolve_native_model(cfg) == "openai:gpt-5.5"


class TestCredentialSource:
    def test_save_then_load_roundtrip(self, tmp_path):
        from pydantic_ai.providers.openai_codex import OpenAICodexCredentials

        path = tmp_path / "auth.json"
        path.write_text(
            json.dumps({"tokens": {"access_token": "a", "refresh_token": "r", "account_id": "acc"}, "other": 1}),
            encoding="utf-8",
        )
        source = CodexFileCredentialSource(path)
        creds = asyncio.run(source.load())
        assert creds.access_token == "a"

        rotated = OpenAICodexCredentials(access_token="a2", refresh_token="r2", account_id="acc")
        asyncio.run(source.save(rotated))
        reloaded = asyncio.run(source.load())
        assert reloaded.access_token == "a2"
        assert reloaded.refresh_token == "r2"
        # Atomic write: no temp files left, unrelated keys preserved.
        assert not list(tmp_path.glob("*.tmp"))
        assert json.loads(path.read_text(encoding="utf-8"))["other"] == 1

    def test_corrupt_load_is_actionable(self, tmp_path):
        from pydantic_ai.exceptions import UserError

        path = tmp_path / "auth.json"
        path.write_text("not json", encoding="utf-8")
        with pytest.raises(UserError, match="codex login"):
            asyncio.run(CodexFileCredentialSource(path).load())

    def test_missing_load_is_actionable(self, tmp_path):
        from pydantic_ai.exceptions import UserError

        with pytest.raises(UserError, match="NATIVE_CREDENTIAL_PATH"):
            asyncio.run(CodexFileCredentialSource(tmp_path / "absent.json").load())

    def test_save_creates_file_when_missing(self, tmp_path):
        from pydantic_ai.providers.openai_codex import OpenAICodexCredentials

        path = tmp_path / "auth.json"
        source = CodexFileCredentialSource(path)
        creds = OpenAICodexCredentials(access_token="a", refresh_token="r", account_id="acc")
        asyncio.run(source.save(creds))
        assert json.loads(path.read_text(encoding="utf-8"))["tokens"]["access_token"] == "a"

    def test_save_writes_owner_only_mode(self, tmp_path):
        from pydantic_ai.providers.openai_codex import OpenAICodexCredentials

        path = tmp_path / "auth.json"
        path.write_text("{}", encoding="utf-8")
        source = CodexFileCredentialSource(path)
        creds = OpenAICodexCredentials(access_token="a", refresh_token="r", account_id="acc")
        asyncio.run(source.save(creds))
        assert (path.stat().st_mode & 0o777) == 0o600


class TestPrefixParity:
    def test_prefix_matches_historical_single_pi_text(self, tmp_path):
        """Regression guard for the prefix extraction refactor."""
        from reviewforge.reasoning.prefix import _build_single_pi_prefix

        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, tmp_path / "repo")
        expected = (
            "Single-call reasoning review for Azure DevOps PR #42.\n"
            "Return only the rich ReviewResult JSON object defined in the system prompt.\n"
            "\nRepository/project metadata:\n"
            '{"status": "active", "isDraft": false}\n'
            "\nChanged files:\na.py\n"
            "\n\nCommits in this PR:\nabc add x"
        )
        assert _build_single_pi_prefix(ctx) == expected

    def test_native_intro_replaces_opening_lines_only(self, tmp_path):
        from reviewforge.reasoning.native import _build_user_prompt

        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, tmp_path / "repo")
        prompt = _build_user_prompt(ctx, "diff --git a/a.py b/a.py\n+ x = 1\n")
        assert prompt.startswith(
            "Native in-process reasoning review for Azure DevOps PR #42.\n"
            "Report each confirmed issue with the record_finding tool"
        )
        assert "\nChanged files:\na.py\n" in prompt
        assert "Unified diff:" in prompt
        assert prompt.endswith("defined in the system prompt.\n")
