"""Focused unit tests for the reviewforge package.

Covers:
- Config loading, env aliases, and CLI precedence
- Artifact manager
- Diff chunker
- Prompt assembly
- Pi runner (with subprocess monkeypatched)
- Validation
- Idempotent posting (dedupe_key, existing markers, should_post)
- Diff line mapper
- Schemas
- Stage runner and one representative stage
- CLI parser
"""
from __future__ import annotations

import json
import subprocess
import sys
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"

from reviewforge.ado import client as ado_client  # noqa: E402
from reviewforge.ado import diff_mapper  # noqa: E402
from reviewforge.ado import posting as ad_posting  # noqa: E402
from reviewforge.ai import prompts  # noqa: E402
from reviewforge.ai.runner import PiRunner, strip_json_fences  # noqa: E402
from reviewforge.artifacts import builder, manager  # noqa: E402
from reviewforge.config import Config, ConfigError, env, is_true, require_uint  # noqa: E402
from reviewforge.exceptions import GitOperationError  # noqa: E402
from reviewforge.git import chunker  # noqa: E402
from reviewforge.git import ops as git_ops  # noqa: E402
from reviewforge.pipeline import orchestrator  # noqa: E402
from reviewforge.pipeline import schemas as pipeline_schemas  # noqa: E402
from reviewforge.pipeline.schemas import (  # noqa: E402
    ContextDigest,
    ContextPlan,
    Finding,
    Intent,
    ReviewDoc,
)
from reviewforge.pipeline.stage import (  # noqa: E402
    Stage,
    StageContext,
    StageStatus,
    run_stages,
)
from reviewforge.pipeline.stages import (  # noqa: E402
    DEFAULT_PIPELINE,
    REVIEW_ONLY_PIPELINE,
    CollectContextStage,
    PostToAdoStage,
    ReviewDiffStage,
)
from reviewforge.pipeline.validation import (  # noqa: E402
    StageLabel,
    validate_review_doc,
    validate_stage,
)




def make_cfg(tmp_path: Path, **overrides) -> Config:
    files = {}
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards", "ac_coverage"]:
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
        ac_coverage_prompt_path=files["ac_coverage"],
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


# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------


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
        with pytest.raises((SystemExit, ConfigError)):
            require_uint("LIMIT", "abc")

    def test_env_uses_default_for_missing(self, monkeypatch):
        monkeypatch.delenv("MISSING_ENV", raising=False)
        assert env("MISSING_ENV", "fallback") == "fallback"

    def test_env_requires_value_without_default(self, monkeypatch):
        monkeypatch.delenv("MISSING_ENV", raising=False)
        with pytest.raises(ConfigError):
            env("MISSING_ENV")

    def test_from_env_parses_pr_url_for_pr_id(self, tmp_path, monkeypatch):
        for key in ["ADO_ORG", "ADO_PROJECT", "ADO_REPO_ID", "PR_ID"]:
            monkeypatch.delenv(key, raising=False)
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("PR_URL", "https://dev.azure.com/org/Proj/_git/repo/pullrequest/7")
        monkeypatch.setenv("WORKSPACE", str(tmp_path))
        cfg = Config.from_env()
        # ``from_env`` only extracts the PR id from a URL; org/project/repo
        # still come from their own env vars (or CLI flags via from_sources).
        assert cfg.pr_id == "7"
        assert cfg.max_diff_bytes == 200000

    def test_from_sources_parses_pr_url_for_all_fields(self, tmp_path):
        env_map = {
            "ADO_AUTH_TOKEN": "tok",
            "PR_URL": "https://dev.azure.com/org/Proj/_git/repo/pullrequest/7",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert (cfg.ado_org, cfg.ado_project, cfg.ado_repo_id, cfg.pr_id) == ("org", "Proj", "repo", "7")

    def test_validate_files_rejects_missing_prompt(self, tmp_path):
        cfg = make_cfg(tmp_path, review_prompt_path=tmp_path / "missing.md")
        with pytest.raises(ConfigError):
            cfg.validate_files()

    def test_from_sources_cli_overrides_env(self, tmp_path):
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "ADO_ORG": "env-org",
            "ADO_PROJECT": "P",
            "ADO_REPO_ID": "R",
            "PR_ID": "1",
        }
        cfg = Config.from_sources(
            {"ado_org": "cli-org", "review_language": "German"},
            env=env_map,
        )
        assert cfg.ado_org == "cli-org"
        assert cfg.review_language == "German"
        assert cfg.ado_project == "P"

    def test_from_sources_ac_coverage_llm_settings(self):
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "ADO_ORG": "o", "ADO_PROJECT": "P", "ADO_REPO_ID": "R", "PR_ID": "1",
            "AC_COVERAGE_LLM": "1",
            "AC_COVERAGE_LLM_MAX_ACS": "5",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.ac_coverage_llm is True
        assert cfg.ac_coverage_llm_max_acs == 5

    def test_from_sources_ac_coverage_llm_disabled_by_default(self):
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "ADO_ORG": "o", "ADO_PROJECT": "P", "ADO_REPO_ID": "R", "PR_ID": "1",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.ac_coverage_llm is False
        assert cfg.ac_coverage_llm_max_acs == 10

    def test_from_sources_resolves_token_aliases(self):
        env_map = {"ADO_MCP_AUTH_TOKEN": "mcp-tok", "ADO_ORG": "x", "ADO_PROJECT": "P", "ADO_REPO_ID": "R", "PR_ID": "1"}
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.ado_token == "mcp-tok"

    def test_from_sources_extracts_pr_id_from_url(self):
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "ADO_ORG": "x",
            "ADO_PROJECT": "P",
            "ADO_REPO_ID": "R",
            "PR_URL": "https://dev.azure.com/x/P/_git/R/pullrequest/99",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.pr_id == "99"
        assert cfg.pr_url == env_map["PR_URL"]

    def test_from_sources_raises_without_token(self, monkeypatch):
        for key in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(key, raising=False)
        with pytest.raises(ConfigError):
            Config.from_sources({"ado_org": "x"})

    def test_validate_for_command_lists_missing_keys(self, tmp_path):
        cfg = make_cfg(tmp_path, ado_token="", ado_org="", ado_project="P", ado_repo_id="R", pr_id="")
        problems = cfg.validate_for_command("review")
        assert any("ADO_AUTH_TOKEN" in p for p in problems)
        assert any("ADO_ORG" in p for p in problems)
        assert any("PR_ID" in p for p in problems)

    def test_validate_for_command_open_prs_does_not_require_pr_id(self, tmp_path):
        cfg = make_cfg(tmp_path, pr_id="")
        problems = cfg.validate_for_command("open-prs")
        assert not any("PR_ID" in p for p in problems)

    def test_env_returns_default_for_empty_string(self, monkeypatch):
        # `env()` treats both `None` and `""` as missing.
        monkeypatch.setenv("X", "")
        assert env("X", "fallback") == "fallback"

    def test_read_env_with_aliases_returns_none_for_unknown_key(self, monkeypatch):
        # No alias entry for this key; the function falls back to KEY.upper().
        monkeypatch.delenv("UNKNOWN_KEY", raising=False)
        from reviewforge.config import _read_env_with_aliases
        assert _read_env_with_aliases("unknown_key") is None

    def test_read_env_with_aliases_uses_callable_env_map(self):
        # When an env map is passed, it is used instead of os.environ.
        env_map = {"MY_ALIAS": "value"}
        from reviewforge.config import _read_env_with_aliases
        assert _read_env_with_aliases("ado_token", env=env_map) is None
        # For the "ado_token" key, the function walks the alias list and
        # returns the first hit. Pass a map with one of those names.
        env_map2 = {"ADO_MCP_AUTH_TOKEN": "mcp"}
        assert _read_env_with_aliases("ado_token", env=env_map2) == "mcp"

    def test_from_env_raises_without_token(self, monkeypatch):
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(k, raising=False)
        with pytest.raises(ConfigError):
            Config.from_env()

    def test_resolve_prompt_path_returns_default_when_unset(self, monkeypatch):
        monkeypatch.delenv("REVIEW_PROMPT_PATH", raising=False)
        from reviewforge.config import _resolve_prompt_path
        result = _resolve_prompt_path("REVIEW_PROMPT_PATH", "/default/path.md")
        assert str(result) == "/default/path.md"

    def test_resolve_prompt_path_uses_env_value(self, monkeypatch):
        monkeypatch.setenv("REVIEW_PROMPT_PATH", "/custom/p.md")
        from reviewforge.config import _resolve_prompt_path
        result = _resolve_prompt_path("REVIEW_PROMPT_PATH", "/default/p.md")
        assert str(result) == "/custom/p.md"

    def test_extract_pr_id_from_url_empty(self):
        from reviewforge.config import _extract_pr_id_from_url
        assert _extract_pr_id_from_url("") == ""

    def test_extract_pr_id_from_url_no_pullrequest(self):
        from reviewforge.config import _extract_pr_id_from_url
        assert _extract_pr_id_from_url("https://example.com/no-match") == ""

    def test_coerce_cli_value_handles_bool_int_float(self):
        from reviewforge.config import _coerce_cli_value
        assert _coerce_cli_value("dry_run", True) is True
        assert _coerce_cli_value("max_diff_bytes", 100) == 100
        assert _coerce_cli_value("max_diff_bytes", 100.5) == 100.5
        assert _coerce_cli_value("dry_run", "1") is True
        assert _coerce_cli_value("dry_run", "0") is False
        assert _coerce_cli_value("ac_coverage_llm", "1") is True
        assert _coerce_cli_value("ac_coverage_llm", "0") is False
        assert _coerce_cli_value("ac_coverage_llm_max_acs", "20") == 20
        assert _coerce_cli_value("review_artifact_root", "/tmp/art") == Path("/tmp/art")
        # Path fields end with _path.
        assert _coerce_cli_value("review_prompt_path", "/tmp/r.md") == Path("/tmp/r.md")
        assert _coerce_cli_value("ac_coverage_prompt_path", "/tmp/ac.md") == Path("/tmp/ac.md")
        # Non-matching string falls through unchanged.
        assert _coerce_cli_value("ado_org", "contoso") == "contoso"
        # None passes through.
        assert _coerce_cli_value("ado_org", None) is None
        # Non-string, non-numeric, non-bool passes through.
        assert _coerce_cli_value("x", ["a"]) == ["a"]

    def test_from_sources_uses_ado_api_key_alias(self):
        env_map = {
            "ADO_API_KEY": "api-key",
            "ADO_ORG": "o", "ADO_PROJECT": "P", "ADO_REPO_ID": "R", "PR_ID": "1",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.ado_token == "api-key"

    def test_from_sources_parses_url_when_org_not_in_env(self, tmp_path, monkeypatch):
        # Only PR_URL is set, so the URL parser must fill in org/project/repo.
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "PR_URL": "https://dev.azure.com/autoorg/Pay/_git/api/pullrequest/55",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.ado_org == "autoorg"
        assert cfg.ado_project == "Pay"
        assert cfg.ado_repo_id == "api"
        assert cfg.pr_id == "55"

    def test_from_sources_unparseable_url_falls_back_safely(self):
        # A bogus URL should not crash; pr_id stays empty.
        env_map = {
            "ADO_AUTH_TOKEN": "t",
            "ADO_ORG": "o", "ADO_PROJECT": "P", "ADO_REPO_ID": "R",
            "PR_URL": "https://example.com/bogus",
        }
        cfg = Config.from_sources({}, env=env_map)
        assert cfg.pr_id == ""


# ---------------------------------------------------------------------------
# parse_dotenv (library helper; PS Import-DotEnv is gone — the
# wrappers no longer parse .env, the user loads it into the shell
# themselves and Python direct callers use parse_dotenv or
# Config.from_env_file explicitly when they want it).
# ---------------------------------------------------------------------------


class TestParseDotenv:
    def test_returns_empty_dict_for_missing_file(self, tmp_path):
        from reviewforge.config import parse_dotenv
        assert parse_dotenv(tmp_path / "missing.env") == {}

    def test_simple_key_value(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO=bar\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar"}

    def test_skips_blank_lines(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("\n\nFOO=bar\n\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar"}

    def test_skips_comments(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("# this is a comment\nFOO=bar\n# another comment\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar"}

    def test_strips_double_quotes(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text('FOO="bar baz"\n', encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar baz"}

    def test_strips_single_quotes(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO='bar baz'\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar baz"}

    def test_preserves_unmatched_quotes(self, tmp_path):
        # Only matching quotes are stripped; this matches the PowerShell
        # behavior and is intentional to keep the parser trivial.
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text('FOO="bar\n', encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": '"bar'}

    def test_strips_whitespace_around_value(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO=  bar  \n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar"}

    def test_value_can_contain_equals(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO=a=b=c\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "a=b=c"}

    def test_value_with_spaces_no_quotes(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO=bar baz\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar baz"}

    def test_ignores_lines_without_equals(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("JUST_A_KEY\nFOO=bar\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "bar"}

    def test_handles_multiple_keys(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text(
            "ADO_AUTH_TOKEN=secret\n"
            "PR_ID=42\n"
            "REVIEW_LANGUAGE=German\n",
            encoding="utf-8",
        )
        result = parse_dotenv(p)
        assert result == {
            "ADO_AUTH_TOKEN": "secret",
            "PR_ID": "42",
            "REVIEW_LANGUAGE": "German",
        }

    def test_last_value_wins_for_duplicate_key(self, tmp_path):
        from reviewforge.config import parse_dotenv
        p = tmp_path / ".env"
        p.write_text("FOO=first\nFOO=second\n", encoding="utf-8")
        assert parse_dotenv(p) == {"FOO": "second"}


# ---------------------------------------------------------------------------
# Config.from_env_file (explicit file loader for direct Python
# callers; the PowerShell wrappers do NOT use this — they read the
# live process env only and expect the user to load the .env file
# themselves before invoking the script).
# ---------------------------------------------------------------------------


class TestConfigFromEnvFile:
    def test_loads_file_values(self, tmp_path, monkeypatch):
        monkeypatch.delenv("SYSTEM_ACCESSTOKEN", raising=False)
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.delenv("ADO_MCP_AUTH_TOKEN", raising=False)
        from reviewforge.config import Config
        p = tmp_path / ".env"
        p.write_text("ADO_AUTH_TOKEN=tok\nADO_ORG=org\n", encoding="utf-8")
        # Other values come from process env or defaults.
        monkeypatch.delenv("ADO_PROJECT", raising=False)
        monkeypatch.delenv("ADO_REPO_ID", raising=False)
        monkeypatch.delenv("PR_ID", raising=False)
        cfg = Config.from_env_file(p)
        assert cfg.ado_token == "tok"
        assert cfg.ado_org == "org"

    def test_process_env_overrides_file(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        p = tmp_path / ".env"
        p.write_text("ADO_ORG=from-file\n", encoding="utf-8")
        monkeypatch.setenv("ADO_ORG", "from-env")
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        cfg = Config.from_env_file(p)
        # Process env wins.
        assert cfg.ado_org == "from-env"

    def test_cli_overrides_everything(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        p = tmp_path / ".env"
        p.write_text("ADO_ORG=from-file\n", encoding="utf-8")
        cfg = Config.from_env_file(p, {"ado_org": "from-cli"})
        assert cfg.ado_org == "from-cli"

    def test_missing_file_falls_back_to_env(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        monkeypatch.setenv("ADO_AUTH_TOKEN", "env-tok")
        cfg = Config.from_env_file(tmp_path / "missing.env")
        assert cfg.ado_token == "env-tok"

    def test_default_path_is_dotenv_in_cwd(self, tmp_path, monkeypatch, capsys):
        monkeypatch.delenv("SYSTEM_ACCESSTOKEN", raising=False)
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        from reviewforge.config import Config
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.delenv("ADO_MCP_AUTH_TOKEN", raising=False)
        # The CLI's default for ``--env-file`` is ``.env`` in cwd. We
        # don't actually change cwd here (pytest doesn't let us easily);
        # just verify the parameter is wired and accepts a Path.
        p = tmp_path / ".env"
        p.write_text("ADO_AUTH_TOKEN=tok\n", encoding="utf-8")
        cfg = Config.from_env_file(p)
        assert cfg.ado_token == "tok"


# ---------------------------------------------------------------------------
# ADO client
# ---------------------------------------------------------------------------


class TestAdoClient:
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
        assert calls[0][1:3] == ["-m", "reviewforge.ado.cli"]
        assert calls[0][3] == "fetch-context"
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


# ---------------------------------------------------------------------------
# Artifacts
# ---------------------------------------------------------------------------


class TestArtifacts:
    def test_create_run_scoped_artifacts_and_latest_pointer(self, tmp_path):
        cfg = make_cfg(tmp_path, review_artifact_root=tmp_path / "artifacts", review_run_id="stable")
        artifacts = manager.create(cfg)
        assert artifacts.dir == tmp_path / "artifacts" / "pr-42" / "runs" / "stable"
        assert (tmp_path / "artifacts" / "pr-42" / "latest.txt").read_text().strip() == str(artifacts.dir)
        assert (artifacts.dir / "run-id.txt").read_text().strip() == "stable"
        # The new file names are part of the contract.
        for name in (
            "metadata.json", "diff.patch", "changed-files.json", "commits.txt",
            "intent.json", "context-plan.json", "context-digest.json",
            "candidate-findings.json", "verified-findings.json",
            "severity-findings.json", "final-findings.json",
            "posted-comments.json", "run-summary.json",
        ):
            assert (artifacts.dir / name).parent == artifacts.dir

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


# ---------------------------------------------------------------------------
# Diff chunker
# ---------------------------------------------------------------------------


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


# ---------------------------------------------------------------------------
# Prompts
# ---------------------------------------------------------------------------


class TestPrompts:
    def test_system_prompt_includes_language_and_standards(self, tmp_path):
        cfg = make_cfg(tmp_path, review_language="German")
        text = prompts.system_prompt(cfg)
        assert "LANGUAGE" in text
        assert "German" in text
        assert "standards prompt" in text
        # Directive must come AFTER the standards block so it sits in the
        # model's recency window (small models weight the tail most).
        assert text.index("standards prompt") < text.index("LANGUAGE:")

    def test_language_directive_replaces_language_value(self, tmp_path):
        cfg = make_cfg(tmp_path, review_language="German")
        assert "in German" in prompts.language_directive(cfg)
        cfg_fr = make_cfg(tmp_path, review_language="French")
        assert "in French" in prompts.language_directive(cfg_fr)
        assert "in German" not in prompts.language_directive(cfg_fr)

    def test_augment_prompt_file_appends_directive(self, tmp_path):
        source = tmp_path / "verify-findings.md"
        source.write_text("base verify prompt", encoding="utf-8")
        cfg = make_cfg(tmp_path, review_language="German")
        dest = prompts.augment_prompt_file(source, cfg)
        text = dest.read_text(encoding="utf-8")
        assert text.startswith("base verify prompt")
        assert text.rstrip().endswith(
            "value in German. Do NOT translate file paths, identifiers, code."
        )

    def test_augment_prompt_file_is_idempotent(self, tmp_path):
        source = tmp_path / "severity.md"
        source.write_text("base severity prompt", encoding="utf-8")
        cfg = make_cfg(tmp_path, review_language="German")
        dest1 = prompts.augment_prompt_file(source, cfg)
        text1 = dest1.read_text(encoding="utf-8")
        # Re-augmenting the same source must not double-append the directive.
        dest2 = prompts.augment_prompt_file(source, cfg)
        text2 = dest2.read_text(encoding="utf-8")
        assert dest1 == dest2
        assert text1 == text2
        assert text2.count("LANGUAGE: Write every") == 1

    def test_augment_prompt_file_tracks_review_language_change(self, tmp_path):
        source = tmp_path / "review-system.md"
        source.write_text("base", encoding="utf-8")
        cfg_de = make_cfg(tmp_path, review_language="German")
        dest = prompts.augment_prompt_file(source, cfg_de)
        assert "in German" in dest.read_text(encoding="utf-8")
        # Switching language writes a fresh augmented file. The cache in
        # the runner is keyed on (cfg), so this is observable when the
        # runner is rebuilt with a different cfg.
        cfg_fr = make_cfg(tmp_path, review_language="French")
        dest_fr = prompts.augment_prompt_file(source, cfg_fr)
        assert "in French" in dest_fr.read_text(encoding="utf-8")

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
            "metadata": metadata,
            "diff": tmp_path / "diff.patch",
            "work_items": tmp_path / "work-items.json",
            "threads": tmp_path / "threads.json",
        }
        text = prompts.stage_instruction("intent", cfg, metadata, "a.py\n", [], [], paths)
        # Phase B: with sessions enabled, the prompt only references paths.
        assert str(metadata) in text
        assert "Changed files" in text
        assert "read" in text or "grep" in text  # mentions the tools

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
        assert "CHUNK LABEL: chunk 1/2" in text
        assert "truncated" in text
        # Phase B: session path. The intent/digest paths are referenced for
        # optional reading, and we mention the chunk's diff is on stdin.
        assert str(intent_file) in text
        assert str(digest_file) in text
        assert "stdin" in text

    def test_stage_instruction_embeds_in_legacy_mode(self, tmp_path):
        # No sessions: original embed-everything behavior.
        cfg = replace(make_cfg(tmp_path), pi_session_enabled=False)
        metadata = tmp_path / "metadata.json"
        metadata.write_text('{"title":"PR"}', encoding="utf-8")
        paths = {
            "metadata": metadata,
            "diff": tmp_path / "diff.patch",
            "work_items": tmp_path / "work-items.json",
            "threads": tmp_path / "threads.json",
        }
        text = prompts.stage_instruction("intent", cfg, metadata, "a.py\n", [], [], paths)
        assert "Repository/project metadata" in text
        assert "Linked work items" in text
        assert "Existing PR comments" in text


# ---------------------------------------------------------------------------
# Pi runner
# ---------------------------------------------------------------------------


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
        monkeypatch.setattr("reviewforge.ai.runner.subprocess.run", fake_run)
        prompt = tmp_path / "prompt.md"
        prompt.write_text("base prompt", encoding="utf-8")
        output = tmp_path / "pi.json"
        PiRunner(cfg).run_json(prompt, "stdin", output, "stage")
        assert json.loads(output.read_text()) == {"ok": True}
        assert "ADO_AUTH_TOKEN" not in seen_env
        assert "ADO_API_KEY" not in seen_env

    def test_run_json_repairs_invalid_json(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        calls = []

        def fake_run(cmd, input, stdout, stderr, timeout, env):
            calls.append(cmd)
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            return subprocess.CompletedProcess(cmd, 0, b'{"repaired": true}', b"")

        monkeypatch.setattr("reviewforge.ai.runner.subprocess.run", fake_run)
        prompt = tmp_path / "prompt.md"
        prompt.write_text("base prompt", encoding="utf-8")
        output = tmp_path / "pi.json"
        PiRunner(cfg).run_json(prompt, "stdin", output, "stage")
        assert json.loads(output.read_text()) == {"repaired": True}
        assert len(calls) == 2

    def test_run_json_raises_on_nonzero(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        monkeypatch.setattr(
            "reviewforge.ai.runner.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess([], 9, b"", b"bad"),
        )
        prompt = tmp_path / "prompt.md"
        prompt.write_text("base prompt", encoding="utf-8")
        with pytest.raises(SystemExit):
            PiRunner(cfg).run_json(prompt, "stdin", tmp_path / "out.json", "stage")


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------


class TestValidation:
    def test_validate_review_doc_rejects_bad_severity(self):
        with pytest.raises(SystemExit):
            validate_review_doc({"summary": "x", "findings": [{"severity": "critical", "title": "T", "message": "M"}]})

    def test_validate_stage_rejects_missing_intent_fields(self):
        with pytest.raises(SystemExit):
            validate_stage({"pr_intent": "x"}, "intent reconstruction")

    def test_validate_stage_accepts_context_plan(self):
        validate_stage({"files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}, "context planning")


# ---------------------------------------------------------------------------
# Idempotent posting
# ---------------------------------------------------------------------------


class TestIdempotentPosting:
    def test_dedupe_key_stable_across_reruns(self):
        a = {"file": "src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M"}
        b = {"file": "src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M",
             "confidence": "high", "suggestion": "fix it"}
        assert ad_posting.dedupe_key(a) == ad_posting.dedupe_key(b)

    def test_dedupe_key_changes_with_significant_field(self):
        a = {"file": "src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M"}
        b = {"file": "src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M2"}
        assert ad_posting.dedupe_key(a) != ad_posting.dedupe_key(b)

    def test_dedupe_key_normalizes_leading_slash(self):
        a = {"file": "src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M"}
        b = {"file": "/src/app.py", "line": 5, "severity": "major", "title": "T", "message": "M"}
        assert ad_posting.dedupe_key(a) == ad_posting.dedupe_key(b)

    def test_dedupe_key_is_12_hex_chars(self):
        f = {"file": "x.py", "line": 1, "severity": "nit", "title": "T", "message": "M"}
        key = ad_posting.dedupe_key(f)
        assert len(key) == 12
        assert all(c in "0123456789abcdef" for c in key)

    def test_should_post_false_when_marker_present(self):
        f = {"file": "x.py", "line": 1, "severity": "major", "title": "T", "message": "M"}
        key = ad_posting.dedupe_key(f)
        assert not ad_posting.should_post(f, {key})

    def test_should_post_true_when_marker_absent(self):
        f = {"file": "x.py", "line": 1, "severity": "major", "title": "T", "message": "M"}
        assert ad_posting.should_post(f, set())
        assert ad_posting.should_post(f, {"other-key"})

    def test_existing_bot_markers_extracts_keys(self):
        threads = [
            {"comments": [{"content": "First comment\nprb:abc123def0\n"}]},
            {"comments": [{"content": "No marker here"}]},
            {"comments": [{"content": "Multi\nprb:000111222333\nmore\n"}]},
        ]
        markers = ad_posting.existing_bot_markers(threads)
        assert markers == {"abc123def0", "000111222333"}

    def test_classify_threads_separates_bot_and_human(self):
        threads = [
            {"comments": [{"content": "Bot comment\nprb:abc123def0\n"}]},
            {"comments": [{"content": "Human comment"}]},
        ]
        cls = ad_posting.classify_threads(threads)
        assert cls.bot == {"abc123def0"}
        assert cls.human == 1
        assert cls.count == 1

    def test_attach_marker_returns_key_and_text(self):
        f = {"file": "x.py", "line": 1, "severity": "major", "title": "T", "message": "M"}
        key, marker = ad_posting.attach_marker(f)
        assert key == ad_posting.dedupe_key(f)
        assert marker == f"prb:{key}"


# ---------------------------------------------------------------------------
# Diff line mapper
# ---------------------------------------------------------------------------


class TestDiffMapper:
    DIFF = (
        "diff --git a/src/app.py b/src/app.py\n"
        "index 0000..0001 100644\n"
        "--- a/src/app.py\n"
        "+++ b/src/app.py\n"
        "@@ -1,3 +1,5 @@\n"
        " line1\n"
        "+added1\n"
        "+added2\n"
        " line2\n"
        " line3\n"
        "@@ -10,2 +12,3 @@\n"
        " line10\n"
        "+added3\n"
        " line11\n"
    )

    def test_parse_collects_changed_files(self):
        files = diff_mapper.collect_changed_files(self.DIFF)
        assert files == ["src/app.py"]

    def test_exact_line_maps_to_hunk_start(self):
        ctx = diff_mapper.map_file_line_to_diff_position("src/app.py", 3, diff_text=self.DIFF)
        assert ctx is not None
        assert ctx.right_file_start == 3
        assert ctx.right_file_end == 3
        assert ctx.file_path == "/src/app.py"

    def test_line_in_hunk_uses_hunk_start(self):
        # line 2 is the first added line in the hunk; the block runs to line 3.
        ctx = diff_mapper.map_file_line_to_diff_position("src/app.py", 2, diff_text=self.DIFF)
        assert ctx is not None
        assert ctx.right_file_start == 2
        assert ctx.right_file_end == 3

    def test_line_outside_hunk_falls_back_to_above(self):
        # line 8 is past the first hunk (which ends at 5) and before the second (12).
        ctx = diff_mapper.map_file_line_to_diff_position("src/app.py", 8, diff_text=self.DIFF)
        assert ctx is not None
        # The closest hunk entry at-or-below is the end of the first hunk
        # block (line 3).
        assert ctx.right_file_start == 3
        assert ctx.right_file_end == 3

    def test_line_in_second_hunk_maps_correctly(self):
        # added3 is in the second hunk starting at line 12.
        ctx = diff_mapper.map_file_line_to_diff_position("src/app.py", 13, diff_text=self.DIFF)
        assert ctx is not None
        # The second hunk's only added line is 13.
        assert ctx.right_file_start == 13
        assert ctx.right_file_end == 13

    def test_unknown_file_returns_none(self):
        ctx = diff_mapper.map_file_line_to_diff_position("missing.py", 1, diff_text=self.DIFF)
        assert ctx is None

    def test_no_line_returns_none(self):
        assert diff_mapper.map_file_line_to_diff_position("src/app.py", None, diff_text=self.DIFF) is None
        assert diff_mapper.map_file_line_to_diff_position("src/app.py", 0, diff_text=self.DIFF) is None

    def test_file_level_fallback(self):
        ctx = diff_mapper.map_file_to_fallback("src/app.py", diff_text=self.DIFF)
        assert ctx is not None
        assert ctx.file_path == "/src/app.py"
        assert ctx.right_file_start == 1
        assert ctx.right_file_end == 1

    def test_file_level_fallback_for_mode_only_change(self):
        # Mode-only chmod produces a diff with the file header but no
        # `@@` hunk lines. The file-level fallback must still return
        # something — a file-level context (no line anchor) — so the
        # post step does not silently drop the finding.
        chmod_diff = (
            "diff --git a/Install-Requirements.sh b/Install-Requirements.sh\n"
            "old mode 100755\n"
            "new mode 100644\n"
            "index 1234567..89abcde\n"
            "--- a/Install-Requirements.sh\n"
            "+++ b/Install-Requirements.sh\n"
        )
        ctx = diff_mapper.map_file_to_fallback("Install-Requirements.sh", diff_text=chmod_diff)
        assert ctx is not None
        assert ctx.file_path == "/Install-Requirements.sh"
        assert ctx.is_file_level
        # No line numbers — ADO attaches the comment to the file header.
        assert ctx.right_file_start is None
        assert ctx.right_file_end is None
        ser = ctx.to_thread_context()
        assert ser == {"filePath": "/Install-Requirements.sh"}

    def test_file_level_fallback_for_binary_file(self):
        # Binary files also have no `@@` hunks in the diff.
        binary_diff = (
            "diff --git a/img.png b/img.png\n"
            "index 1234567..89abcde 100644\n"
            "GIT binary patch\n"
            "literal 1234\n"
            "abc...\n"
            "--- a/img.png\n"
            "+++ b/img.png\n"
        )
        ctx = diff_mapper.map_file_to_fallback("img.png", diff_text=binary_diff)
        assert ctx is not None
        assert ctx.is_file_level
        assert ctx.to_thread_context() == {"filePath": "/img.png"}

    def test_file_level_fallback_for_unknown_file(self):
        # File not in diff at all → still None.
        ctx = diff_mapper.map_file_to_fallback("missing.py", diff_text=self.DIFF)
        assert ctx is None

    def test_to_thread_context_serializes(self):
        ctx = diff_mapper.map_file_line_to_diff_position("src/app.py", 2, diff_text=self.DIFF)
        assert ctx is not None
        ser = ctx.to_thread_context()
        assert ser["filePath"] == "/src/app.py"
        assert ser["rightFileStart"]["line"] == 2
        assert ser["rightFileEnd"]["line"] == 3

    def test_renamed_file(self):
        renamed_diff = (
            "diff --git a/old.py b/new.py\n"
            "similarity index 90%\n"
            "rename from old.py\n"
            "rename to new.py\n"
            "--- a/old.py\n"
            "+++ b/new.py\n"
            "@@ -1,2 +1,3 @@\n"
            " line1\n"
            "+added\n"
            " line2\n"
        )
        ctx = diff_mapper.map_file_line_to_diff_position("new.py", 2, diff_text=renamed_diff)
        assert ctx is not None
        assert ctx.file_path == "/new.py"


# ---------------------------------------------------------------------------
# Schemas
# ---------------------------------------------------------------------------


class TestSchemas:
    def test_intent_valid(self):
        Intent(pr_intent="Fix X", changed_behaviors=["a"], risk_areas=["b"])

    def test_intent_requires_nonempty_pr_intent(self):
        with pytest.raises(Exception):
            Intent(pr_intent="")

    def test_context_plan_default_lists(self):
        cp = ContextPlan()
        assert cp.files_to_read == []
        assert cp.searches_to_run == []
        assert cp.tests_to_inspect == []

    def test_finding_normalizes_invalid_severity(self):
        with pytest.raises(Exception):
            Finding(severity="critical", title="T", message="M")  # type: ignore[arg-type]

    def test_review_doc_rejects_empty_summary(self):
        with pytest.raises(Exception):
            ReviewDoc(summary="", findings=[])

    def test_finding_normalizes_missing_required_field(self):
        with pytest.raises(Exception):
            Finding.model_validate({"severity": "major"})

    def test_load_and_validate_validates_file(self, tmp_path):
        path = tmp_path / "intent.json"
        path.write_text(json.dumps({"pr_intent": "Fix X", "changed_behaviors": [], "risk_areas": []}))
        loaded = pipeline_schemas.load_and_validate(path, Intent)
        assert isinstance(loaded, Intent)


# ---------------------------------------------------------------------------
# Stage runner
# ---------------------------------------------------------------------------


class TestStageRunner:
    def test_run_stages_records_results_in_order(self, tmp_path):
        class Stub(Stage):
            def __init__(self, name):
                self.name = name
                self.calls = 0

            def run(self, ctx):
                self.calls += 1
                return {"calls": self.calls}

        a, b, c = Stub("a"), Stub("b"), Stub("c")
        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        results = run_stages([a, b, c], ctx)
        assert [r.name for r in results] == ["a", "b", "c"]
        assert [r.status for r in results] == [StageStatus.OK] * 3

    def test_stage_failure_short_circuits(self, tmp_path):
        class BadStage(Stage):
            name = "bad"
            def run(self, ctx):
                raise SystemExit("boom")

        class GoodStage(Stage):
            name = "good"
            def run(self, ctx):
                return {"ok": True}

        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        results = run_stages([BadStage(), GoodStage()], ctx)
        assert results[0].status == StageStatus.FAILED
        assert "boom" in results[0].error
        assert len(results) == 1

    def test_should_run_can_skip(self, tmp_path):
        class SkippedStage(Stage):
            name = "skipped"
            def should_run(self, ctx):
                return False
            def run(self, ctx):
                raise AssertionError("should not run")

        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        result = SkippedStage()(ctx)
        assert result.status == StageStatus.SKIPPED

    def test_collect_context_stage_reads_safe_files(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        (tmp_path / "a.py").write_text("print('hello')\n", encoding="utf-8")
        builder.write_json(
            artifacts.plan,
            {
                "files_to_read": [{"path": "a.py", "reason": "changed"}, {"path": "../secret", "reason": "bad"}],
                "tests_to_inspect": ["a.py"],
                "searches_to_run": [{"query": "hello", "reason": "callsite"}],
            },
        )
        state = SimpleNamespace(repo_dir=tmp_path, files=["a.py"], range_spec="x", diff_text="d", target_branch="m", source_branch="f", target_commit="t", source_commit="s", base_commit="b")
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=MagicMock())
        ctx.plan = builder.read_json(artifacts.plan)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.collect_context.subprocess.run",
            lambda *a, **k: subprocess.CompletedProcess(a, 0, b"a.py:1:hello\n", b""),
        )
        result = CollectContextStage()(ctx)
        assert result.status == StageStatus.OK
        doc = builder.read_json(artifacts.collected)
        assert doc["files"][0]["path"] == "a.py"

    def test_review_diff_stage_writes_candidate(self, tmp_path):
        cfg = make_cfg(tmp_path)
        artifacts = manager.create(cfg)
        state = SimpleNamespace(repo_dir=tmp_path, files=["a.py"], range_spec="x", diff_text="d", target_branch="m", source_branch="f", target_commit="t", source_commit="s", base_commit="b")
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=MagicMock())
        ctx.files_text = "a.py\n"
        ctx.extras["system_prompt"] = "sys"
        def fake_run_json(_prompt, stdin, out, stage):
            builder.write_json(out, {"summary": "ok", "findings": []})
        ctx.pi.run_json.side_effect = fake_run_json
        result = ReviewDiffStage()(ctx)
        assert result.status == StageStatus.OK
        assert builder.read_json(artifacts.candidate)["summary"] == "ok"

    def test_post_stage_dry_run_does_not_call_helper(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, dry_run=True)
        artifacts = manager.create(cfg)
        builder.write_json(artifacts.severity, {"summary": "ok", "findings": []})
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert called == []
        assert ctx.posted.get("dry_run") == 1

    def test_post_stage_calls_helper_when_not_dry_run(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, dry_run=False)
        artifacts = manager.create(cfg)
        builder.write_json(artifacts.severity, {"summary": "ok", "findings": []})
        ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=MagicMock())
        called = []
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.post_to_ado.call_helper",
            lambda *a, **k: called.append((a, k)),
        )
        result = PostToAdoStage()(ctx)
        assert result.status == StageStatus.OK
        assert len(called) == 1
        assert called[0][0][1] == "post-findings"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


class TestCli:
    def test_review_parses_minimum(self):
        from reviewforge.cli import build_parser
        args = build_parser().parse_args(["review", "--pr", "42", "--org", "x"])
        assert args.command == "review"
        assert args.pr_id == "42"
        assert args.ado_org == "x"

    def test_post_requires_input(self, monkeypatch, capsys):
        from reviewforge.cli import build_parser, main
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "x")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "42")
        rc = main(["post"])
        assert rc == 2
        assert "--input" in capsys.readouterr().err

    def test_open_prs_returns_error_with_powershell_hint(self, capsys):
        from reviewforge.cli import main
        rc = main(["open-prs"])
        assert rc == 2
        err = capsys.readouterr().err
        assert "not supported" in err
        assert "run-open-prs.ps1" in err

    def test_validate_config_succeeds(self, monkeypatch, tmp_path, capsys):
        for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
            (tmp_path / f"{name}.md").write_text(f"{name}", encoding="utf-8")
        for key in (
            "ADO_AUTH_TOKEN", "ADO_ORG", "ADO_PROJECT", "ADO_REPO_ID", "PR_ID",
            "REVIEW_PROMPT_PATH", "INTENT_PROMPT_PATH", "CONTEXT_PLAN_PROMPT_PATH",
            "CONTEXT_DIGEST_PROMPT_PATH", "VERIFY_PROMPT_PATH", "SEVERITY_PROMPT_PATH",
            "REVIEW_STANDARDS_PATH", "REVIEW_RUN_ID", "AC_COVERAGE_LLM",
            "SYSTEM_ACCESSTOKEN",
        ):
            monkeypatch.delenv(key, raising=False)
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "org")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "1")
        monkeypatch.setenv("REVIEW_PROMPT_PATH", str(tmp_path / "review.md"))
        monkeypatch.setenv("INTENT_PROMPT_PATH", str(tmp_path / "intent.md"))
        monkeypatch.setenv("CONTEXT_PLAN_PROMPT_PATH", str(tmp_path / "plan.md"))
        monkeypatch.setenv("CONTEXT_DIGEST_PROMPT_PATH", str(tmp_path / "digest.md"))
        monkeypatch.setenv("VERIFY_PROMPT_PATH", str(tmp_path / "verify.md"))
        monkeypatch.setenv("SEVERITY_PROMPT_PATH", str(tmp_path / "severity.md"))
        monkeypatch.setenv("REVIEW_STANDARDS_PATH", str(tmp_path / "standards.md"))
        from reviewforge.cli import main
        rc = main(["validate-config"])
        assert rc == 0, capsys.readouterr()
        out = capsys.readouterr().out
        assert "valid" in out

    def test_validate_config_fails_on_missing_org(self, monkeypatch, capsys):
        for key in (
            "ADO_AUTH_TOKEN", "ADO_ORG", "ADO_PROJECT", "ADO_REPO_ID", "PR_ID",
        ):
            monkeypatch.delenv(key, raising=False)
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        from reviewforge.cli import main
        rc = main(["validate-config"])
        assert rc == 1
        err = capsys.readouterr().err
        assert "ADO_ORG" in err



# ---------------------------------------------------------------------------
# Git ops
# ---------------------------------------------------------------------------


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
        with pytest.raises(GitOperationError):
            git_ops.run_logged("step", ["git", "status"], tmp_path)

    def test_prepare_repo_and_cleanup(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, clone_root=tmp_path / "clones")
        commands: list[list[str]] = []


        def fake_run(cmd, cwd=None, stdout=None, stderr=None, env=None):
            commands.append(cmd)
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
        assert sum("--depth=200" in cmd for cmd in commands) == 2
        assert not any("--deepen=" in " ".join(cmd) or "--unshallow" in cmd for cmd in commands)
        git_ops.cleanup(state)

    def test_prepare_repo_deepens_until_merge_base_exists(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, clone_root=tmp_path / "clones")
        commands: list[list[str]] = []
        merge_base_calls = 0

        def fake_logged(desc, cmd, cwd):
            commands.append(cmd)

        def fake_run(cmd, cwd=None, stdout=None, stderr=None, env=None):
            nonlocal merge_base_calls
            if cmd[:2] == ["git", "merge-base"]:
                merge_base_calls += 1
                return subprocess.CompletedProcess(cmd, 0 if merge_base_calls >= 3 else 1, b"base123\n", b"")
            if cmd[:2] == ["git", "rev-parse"]:
                return subprocess.CompletedProcess(cmd, 0, b"commit123\n", b"")
            if cmd[:2] == ["git", "diff"] and "--name-only" in cmd:
                return subprocess.CompletedProcess(cmd, 0, b"src/a.py\n", b"")
            if cmd[:2] == ["git", "diff"]:
                return subprocess.CompletedProcess(cmd, 0, b"difftext", b"")
            return subprocess.CompletedProcess(cmd, 0, b"", b"")

        monkeypatch.setattr(git_ops, "run_logged", fake_logged)
        monkeypatch.setattr(git_ops.subprocess, "run", fake_run)
        state = git_ops.prepare_repo(cfg, "feature/x", "main")
        assert state.base_commit == "base123"
        assert [cmd[3] for cmd in commands if "--deepen=1000" in cmd or "--deepen=5000" in cmd] == [
            "--deepen=1000",
            "--deepen=1000",
            "--deepen=5000",
            "--deepen=5000",
        ]
        assert not any("--unshallow" in cmd for cmd in commands)
        git_ops.cleanup(state)

    def test_prepare_repo_reports_missing_merge_base_after_unshallow(self, tmp_path, monkeypatch):
        cfg = make_cfg(tmp_path, clone_root=tmp_path / "clones")
        commands: list[list[str]] = []

        monkeypatch.setattr(git_ops, "run_logged", lambda desc, cmd, cwd: commands.append(cmd))
        monkeypatch.setattr(
            git_ops.subprocess,
            "run",
            lambda cmd, **kwargs: subprocess.CompletedProcess(
                cmd,
                1 if cmd[:2] == ["git", "merge-base"] else 0,
                b"",
                b"",
            ),
        )

        with pytest.raises(GitOperationError, match=r"main.*feature/x.*\[200, 1200, 6200, 10000\]"):
            git_ops.prepare_repo(cfg, "feature/x", "main")
        assert [cmd[3] for cmd in commands if "--deepen=" in " ".join(cmd)] == [
            "--deepen=1000",
            "--deepen=1000",
            "--deepen=5000",
            "--deepen=5000",
            "--deepen=3800",
            "--deepen=3800",
        ]
        assert sum("--unshallow" in cmd for cmd in commands) == 2


# ---------------------------------------------------------------------------
# Default pipeline shape
# ---------------------------------------------------------------------------


class TestDefaultPipeline:
    def test_default_pipeline_covers_all_stages(self):
        names = [stage.name for stage in DEFAULT_PIPELINE]
        assert names == [
            "fetch_pr_metadata",
            "prepare_repository",
            "execute_reasoning_engine",
            "post_to_ado",
        ]

    def test_review_only_pipeline_excludes_posting(self):
        names = [stage.name for stage in REVIEW_ONLY_PIPELINE]
        assert "post_to_ado" not in names
        assert "execute_reasoning_engine" in names
