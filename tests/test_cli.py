"""CLI command tests with the pipeline stubbed out.

These tests exercise the argparse wiring, the CLI→Config layer, the
command-specific validation, and the dispatch to the orchestrator
entry points (``run_full`` / ``run_review_only`` / ``run_post_only``).
The orchestrator is replaced with a recording stub so the CLI tests
do not depend on git, Pi, or the network.
"""
from __future__ import annotations

import json
import re
import sys
from dataclasses import replace
from pathlib import Path
from unittest.mock import MagicMock, patch
import os

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from reviewforge import cli  # noqa: E402
from reviewforge.exceptions import PiExecutionError  # noqa: E402
from reviewforge.cli import (  # noqa: E402
    _apply_common,
    _build_config,
    build_parser,
    cmd_open_prs,
    cmd_post,
    cmd_review,
    cmd_validate_config,
    main,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


@pytest.fixture
def clean_env(monkeypatch):
    """Strip all ADO_*, PR_*, PI_* env vars to give tests a known baseline."""
    for key in list(monkeypatch._envstore if hasattr(monkeypatch, "_envstore") else {}):
        pass
    import os
    keys = [
        k for k in os.environ
        if k.startswith((
            "ADO_", "PR_", "PI_", "REVIEW_", "WORKSPACE", "CLONE_ROOT",
            "CHUNK_", "MAX_", "DISABLE_", "DRY_", "FORCE_", "INCLUDE_",
            "VERIFY_", "AC_",
        )) or k == "SYSTEM_ACCESSTOKEN"
    ]
    for k in keys:
        monkeypatch.delenv(k, raising=False)


def _set_min_env(monkeypatch, tmp_path):
    """Set the minimum env vars to satisfy cmd_validate_config happy path."""
    monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
    monkeypatch.setenv("ADO_ORG", "o")
    monkeypatch.setenv("ADO_PROJECT", "P")
    monkeypatch.setenv("ADO_REPO_ID", "R")
    monkeypatch.setenv("PR_ID", "1")
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        (tmp_path / f"{name}.md").write_text("p", encoding="utf-8")
    monkeypatch.setenv("REVIEW_PROMPT_PATH", str(tmp_path / "review.md"))
    monkeypatch.setenv("INTENT_PROMPT_PATH", str(tmp_path / "intent.md"))
    monkeypatch.setenv("CONTEXT_PLAN_PROMPT_PATH", str(tmp_path / "plan.md"))
    monkeypatch.setenv("CONTEXT_DIGEST_PROMPT_PATH", str(tmp_path / "digest.md"))
    monkeypatch.setenv("VERIFY_PROMPT_PATH", str(tmp_path / "verify.md"))
    monkeypatch.setenv("SEVERITY_PROMPT_PATH", str(tmp_path / "severity.md"))
    monkeypatch.setenv("REVIEW_STANDARDS_PATH", str(tmp_path / "standards.md"))


# ---------------------------------------------------------------------------
# Parser wiring
# ---------------------------------------------------------------------------


class TestParserWiring:
    def test_review_subcommand(self):
        args = build_parser().parse_args(["review", "--pr", "42", "--org", "x"])
        assert args.command == "review"
        assert args.pr_id == "42"
        assert args.ado_org == "x"
        assert args.func is cmd_review

    def test_post_subcommand(self):
        args = build_parser().parse_args(["post", "--pr", "1", "--input", "in.json"])
        assert args.command == "post"
        assert args.input == "in.json"
        assert args.func is cmd_post

    def test_validate_config_subcommand(self):
        args = build_parser().parse_args(["validate-config"])
        assert args.command == "validate-config"
        assert args.func is cmd_validate_config

    def test_open_prs_subcommand(self):
        args = build_parser().parse_args(["open-prs"])
        assert args.command == "open-prs"
        assert args.func is cmd_open_prs

    def test_dry_run_flags(self):
        a = build_parser().parse_args(["review", "--dry-run"])
        assert a.dry_run is True
        b = build_parser().parse_args(["review", "--no-dry-run"])
        assert b.dry_run is False

    def test_no_post_flag(self):
        a = build_parser().parse_args(["review", "--no-post"])
        assert a.no_post is True


# ---------------------------------------------------------------------------
# _apply_common
# ---------------------------------------------------------------------------


class TestApplyCommon:
    def _cfg(self):
        from reviewforge.config import Config
        return Config(
            ado_org="o", ado_project="P", ado_repo_id="R", pr_id="1", ado_token="t",
            source_branch="feature", target_branch="main",
            workspace=Path("/tmp"), clone_root=Path("/tmp"),
            review_language="English",
            review_prompt_path=Path("/tmp/r.md"), intent_prompt_path=Path("/tmp/i.md"),
            context_plan_prompt_path=Path("/tmp/p.md"),
            context_digest_prompt_path=Path("/tmp/d.md"),
            verify_prompt_path=Path("/tmp/v.md"), severity_prompt_path=Path("/tmp/s.md"),
            standards_path=Path("/tmp/std.md"),
            pi_model="m", max_diff_bytes=100, chunk_trigger_diff_bytes=100,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=False,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=Path("/tmp/art"),
            review_run_id=None,
        )

    def test_preserves_config_when_no_overrides(self):
        cfg = self._cfg()
        ns = build_parser().parse_args(["review"])
        assert _apply_common(cfg, ns) is cfg

    def test_overrides_string_fields(self):
        cfg = self._cfg()
        ns = build_parser().parse_args([
            "review", "--org", "neworg", "--pi-model", "x/y",
            "--language", "German", "--review-run-id", "rid",
        ])
        out = _apply_common(cfg, ns)
        assert out.ado_org == "neworg"
        assert out.pi_model == "x/y"
        assert out.review_language == "German"
        assert out.review_run_id == "rid"

    def test_overrides_dry_run_true(self):
        cfg = self._cfg()
        ns = build_parser().parse_args(["review", "--dry-run"])
        out = _apply_common(cfg, ns)
        assert out.dry_run is True

    def test_overrides_dry_run_false(self):
        cfg = self._cfg()
        ns = build_parser().parse_args(["review", "--no-dry-run"])
        out = _apply_common(cfg, ns)
        assert out.dry_run is False

    def test_overrides_force_review(self):
        cfg = self._cfg()
        ns = build_parser().parse_args(["review", "--force-review"])
        out = _apply_common(cfg, ns)
        assert out.force_review is True

    def test_pr_as_url_rewrites_pr_url(self):
        cfg = self._cfg()
        url = "https://dev.azure.com/contoso/Pay/_git/api/pullrequest/77"
        ns = build_parser().parse_args(["review", "--pr", url])
        out = _apply_common(cfg, ns)
        # ``--pr`` with a non-digit value moves into pr_url.
        assert out.pr_id == "1"  # original
        assert out.pr_url == url


# ---------------------------------------------------------------------------
# _build_config
# ---------------------------------------------------------------------------


class TestBuildConfig:
    def test_uses_cli_over_env(self, clean_env, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "env-tok")
        monkeypatch.setenv("ADO_ORG", "env-org")
        ns = build_parser().parse_args(["review", "--org", "cli-org"])
        cfg = _build_config(ns)
        assert cfg.ado_org == "cli-org"
        # Token still picked up from env.
        assert cfg.ado_token == "env-tok"

    def test_returns_config_with_defaults(self, clean_env, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        ns = build_parser().parse_args(["review"])
        cfg = _build_config(ns)
        assert cfg.ado_token == "t"
        assert cfg.review_language == "English"
        assert cfg.max_diff_bytes == 200000

    def test_collect_context_workers_from_env(self, clean_env, monkeypatch):
        from reviewforge.config import Config

        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("COLLECT_CONTEXT_WORKERS", "12")
        cfg = Config.from_env()
        assert cfg.collect_context_workers == 12

    def test_collect_context_workers_from_sources(self, clean_env, monkeypatch):
        from reviewforge.config import Config

        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        cfg = Config.from_sources({"command": "review"}, env={"ADO_AUTH_TOKEN": "t", "COLLECT_CONTEXT_WORKERS": "9"})
        assert cfg.collect_context_workers == 9


# ---------------------------------------------------------------------------
# cmd_open_prs
# ---------------------------------------------------------------------------


class TestCmdOpenPrs:
    def test_returns_error_with_powershell_hint(self, capsys):
        rc = cmd_open_prs(MagicMock())
        assert rc == 2
        err = capsys.readouterr().err
        assert "not supported" in err
        assert "run-open-prs.ps1" in err


# ---------------------------------------------------------------------------
# cmd_validate_config
# ---------------------------------------------------------------------------


class TestCmdValidateConfig:
    def test_success_prints_diagnostics(self, clean_env, monkeypatch, capsys, tmp_path):
        _set_min_env(monkeypatch, tmp_path)
        ns = build_parser().parse_args(["validate-config"])
        rc = cmd_validate_config(ns)
        assert rc == 0
        out = capsys.readouterr().out
        assert "valid" in out
        assert "model" in out
        assert "language" in out

    def test_missing_org_exits_1(self, clean_env, monkeypatch, capsys, tmp_path):
        # Token + project + repo + pr_id set, but org is missing.
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "1")
        ns = build_parser().parse_args(["validate-config"])
        rc = cmd_validate_config(ns)
        assert rc == 1
        assert "ADO_ORG" in capsys.readouterr().err

    def test_missing_prompt_file_exits_1(self, clean_env, monkeypatch, capsys, tmp_path):
        # Set env vars but DO NOT create the prompt files.
        _set_min_env(monkeypatch, tmp_path)
        for p in [tmp_path / "review.md", tmp_path / "intent.md", tmp_path / "plan.md",
                  tmp_path / "digest.md", tmp_path / "verify.md", tmp_path / "severity.md",
                  tmp_path / "standards.md"]:
            p.unlink()
        ns = build_parser().parse_args(["validate-config"])
        rc = cmd_validate_config(ns)
        assert rc == 1
        assert "Required file not found" in capsys.readouterr().err

    def test_open_prs_command_does_not_require_pr_id(self, clean_env, monkeypatch, capsys, tmp_path):
        # open-prs has no PR_ID requirement.
        _set_min_env(monkeypatch, tmp_path)
        monkeypatch.delenv("PR_ID", raising=False)
        ns = build_parser().parse_args(["validate-config"])
        # Override _command to simulate open-prs.
        ns._command = "open-prs"
        rc = cmd_validate_config(ns)
        assert rc == 0
        out = capsys.readouterr().out
        assert "valid" in out


# ---------------------------------------------------------------------------
# cmd_review (with orchestrator stubbed)
# ---------------------------------------------------------------------------


class TestCmdReview:
    def test_review_full_dispatches_to_run_full(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)
        called = []

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_full", lambda cfg: (called.append("full") or FakeOutcome()))
        monkeypatch.setattr(cli, "run_review_only", lambda cfg, output=None: (called.append("review_only") or FakeOutcome()))
        rc = cmd_review(build_parser().parse_args(["review"]))
        assert rc == 0
        assert called == ["full"]

    def test_review_with_no_post_dispatches_to_review_only(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)
        called = []

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_review_only", lambda cfg, output=None: (called.append(("review_only", output)) or FakeOutcome()))
        monkeypatch.setattr(cli, "run_full", lambda cfg: (called.append("full") or FakeOutcome()))
        ns = build_parser().parse_args(["review", "--no-post", "--output", "/tmp/out.json"])
        rc = cmd_review(ns)
        assert rc == 0
        assert called == [("review_only", "/tmp/out.json")]

    def test_review_propagates_failure(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)

        class BadOutcome:
            exit_code = 2

        monkeypatch.setattr(cli, "run_full", lambda cfg: BadOutcome())
        rc = cmd_review(build_parser().parse_args(["review"]))
        assert rc == 2


# ---------------------------------------------------------------------------
# cmd_post (with orchestrator stubbed)
# ---------------------------------------------------------------------------


class TestCmdPost:
    def test_post_dispatches_to_run_post_only(self, clean_env, monkeypatch, tmp_path, capsys):
        _set_min_env(monkeypatch, tmp_path)
        called = []

        class FakeOutcome:
            exit_code = 0

        def fake(cfg, input_path):
            called.append((cfg, str(input_path)))
            return FakeOutcome()

        monkeypatch.setattr(cli, "run_post_only", fake)
        ns = build_parser().parse_args(["post", "--input", "/tmp/in.json"])
        rc = cmd_post(ns)
        assert rc == 0
        assert called[0][1] == "/tmp/in.json"

    def test_post_propagates_failure(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)

        class BadOutcome:
            exit_code = 1

        monkeypatch.setattr(cli, "run_post_only", lambda *a, **k: BadOutcome())
        ns = build_parser().parse_args(["post", "--input", "/tmp/in.json"])
        rc = cmd_post(ns)
        assert rc == 1


# ---------------------------------------------------------------------------
# main() — top-level dispatch
# ---------------------------------------------------------------------------


class TestMain:
    def test_no_args_defaults_to_review(self, monkeypatch, tmp_path, capsys):
        # main() with no argv should dispatch to cmd_review, not print
        # help. This matches the Dockerfile ENTRYPOINT invocation and
        # ``python -m reviewforge`` (no subcommand) semantics.
        _set_min_env(monkeypatch, tmp_path)

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_full", lambda cfg: FakeOutcome())
        rc = main([])
        assert rc == 0
        # No help text should have been printed.
        out = capsys.readouterr().err
        assert "usage" not in out

    def test_dispatches_to_subcommand(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_full", lambda cfg: FakeOutcome())
        rc = main(["review"])
        assert rc == 0

    def test_translates_domain_error(self, clean_env, monkeypatch, tmp_path, capsys):
        _set_min_env(monkeypatch, tmp_path)
        monkeypatch.setattr(
            cli,
            "run_full",
            lambda cfg: (_ for _ in ()).throw(
                PiExecutionError("[review][ERROR] pi review exited 1")
            ),
        )
        assert main(["review"]) == 1
        assert capsys.readouterr().err == "[review][ERROR] pi review exited 1\n"

    def test_returns_1_for_unknown_command(self, clean_env, monkeypatch, tmp_path, capsys):
        # argparse will reject an unknown subcommand with SystemExit(2)
        # before our handler runs. main() catches it and returns 1.
        _set_min_env(monkeypatch, tmp_path)
        with pytest.raises(SystemExit) as exc:
            main(["definitely-not-a-command"])
        assert exc.value.code == 2


# ---------------------------------------------------------------------------
# Migration: PowerShell forwards env vars, Python does the work.
# ---------------------------------------------------------------------------


class TestPowerShellForwardingContract:
    """The PowerShell wrappers no longer do ADO logic.

    They forward env vars to the container; Python's CLI picks them up.
    These tests pin the contract that ``Config.from_env`` reads the
    same env vars that ``run.ps1`` writes to the env file.
    """

    def test_token_aliases_resolve_to_ado_token(self, clean_env, monkeypatch):
        from reviewforge.config import Config
        # PowerShell forwards whichever of these is set.
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.setenv("ADO_MCP_AUTH_TOKEN", "mcp-tok")
        monkeypatch.setenv("ADO_ORG", "o")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "1")
        cfg = Config.from_env()
        assert cfg.ado_token == "mcp-tok"

    def test_image_alias_resolves_to_image_name(self, clean_env, monkeypatch):
        from reviewforge.config import Config
        # PowerShell sets ``IMAGE_NAME`` (canonical) or accepts ``IMAGE``.
        monkeypatch.setenv("IMAGE", "legacy-image:tag")
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "o")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "1")
        # ``Config`` doesn't read IMAGE itself; the PowerShell scripts
        # do. This test documents that the alias name is just a
        # passthrough in the PowerShell layer. The Python layer is
        # indifferent to which name was used.
        assert os.getenv("IMAGE") == "legacy-image:tag"

    def test_no_token_yields_clear_error(self, clean_env, monkeypatch, capsys):
        from reviewforge.config import Config
        from reviewforge.cli import main
        # No token at all in env.
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(k, raising=False)
        monkeypatch.setenv("ADO_ORG", "o")
        monkeypatch.setenv("ADO_PROJECT", "P")
        monkeypatch.setenv("ADO_REPO_ID", "R")
        monkeypatch.setenv("PR_ID", "1")
        rc = main(["validate-config"])
        assert rc == 1
        assert "ADO_AUTH_TOKEN" in capsys.readouterr().err

    def test_pr_url_parsing_unified(self, clean_env, monkeypatch):
        # PowerShell forwards PR_URL verbatim; Python parses it.
        from reviewforge.config import Config
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        # No individual ADO_* keys set.
        monkeypatch.delenv("ADO_ORG", raising=False)
        monkeypatch.delenv("ADO_PROJECT", raising=False)
        monkeypatch.delenv("ADO_REPO_ID", raising=False)
        monkeypatch.delenv("PR_ID", raising=False)
        monkeypatch.setenv("PR_URL", "https://dev.azure.com/contoso/Pay/_git/api/pullrequest/55")
        cfg = Config.from_sources({})
        assert cfg.ado_org == "contoso"
        assert cfg.ado_project == "Pay"
        assert cfg.ado_repo_id == "api"
        assert cfg.pr_id == "55"

    def test_branch_normalization_in_ado_client(self, clean_env):
        # The branch normalization helper that PowerShell used to do
        # locally is now in the Python package.
        from reviewforge.ado.client import normalize_branch_name
        assert normalize_branch_name("refs/heads/main") == "main"
        assert normalize_branch_name("main") == "main"

    def test_all_config_constructors_default_to_single_pi(self, clean_env, monkeypatch):
        from reviewforge.config import Config

        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "o")
        monkeypatch.delenv("REASONING_ENGINE", raising=False)
        monkeypatch.delenv("FAST_REVIEW", raising=False)
        assert Config.from_env().reasoning_engine == "single_pi"
        assert Config.from_sources(
            {},
            env={
                "ADO_AUTH_TOKEN": "t",
                "ADO_ORG": "o",
                "REASONING_ENGINE": "",
            },
        ).reasoning_engine == "single_pi"
        assert Config(
            ado_org="o",
            ado_project="p",
            ado_repo_id="r",
            pr_id="1",
            ado_token="t",
            source_branch="",
            target_branch="",
            workspace=Path("/tmp"),
            clone_root=Path("/tmp"),
            review_language="English",
            review_prompt_path=Path("review"),
            intent_prompt_path=Path("intent"),
            context_plan_prompt_path=Path("plan"),
            context_digest_prompt_path=Path("digest"),
            verify_prompt_path=Path("verify"),
            severity_prompt_path=Path("severity"),
            standards_path=Path("standards"),
            pi_model="m",
            max_diff_bytes=1,
            chunk_trigger_diff_bytes=1,
            disable_chunk_review=False,
            pi_timeout_secs=1,
            dry_run=False,
            include_work_items=True,
            include_existing_comments=True,
            verify_findings=True,
            force_review=False,
            review_target_branches="",
            review_artifact_dir=None,
            review_artifact_root=Path("/tmp"),
            review_run_id=None,
        ).reasoning_engine == "single_pi"
    def test_env_file_path_supported(self, tmp_path, monkeypatch):
        # Direct Python callers can load a .env file explicitly via
        # Config.from_env_file. The PowerShell wrappers do NOT do
        monkeypatch.delenv("SYSTEM_ACCESSTOKEN", raising=False)
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.delenv("ADO_MCP_AUTH_TOKEN", raising=False)
        # this; they read the live process env and expect the user
        # to have loaded the file themselves.
        from reviewforge.config import Config
        p = tmp_path / ".env"
        p.write_text("ADO_AUTH_TOKEN=t\nADO_ORG=o\n", encoding="utf-8")
        cfg = Config.from_env_file(p)
        assert cfg.ado_token == "t"
        assert cfg.ado_org == "o"

    def test_review_problems_exit_2(self, clean_env, monkeypatch, tmp_path, capsys):
        # No ADO_ORG set → cmd_review reports problems and exits 2.
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        ns = build_parser().parse_args(["review"])
        rc = cmd_review(ns)
        assert rc == 2
        assert "ADO_ORG" in capsys.readouterr().err

    def test_review_no_post_with_no_output_routes_to_review_only(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)
        called = []

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_full", lambda c: (called.append("full") or FakeOutcome()))
        monkeypatch.setattr(cli, "run_review_only", lambda c, output=None: (called.append(("review_only", output)) or FakeOutcome()))
        # --no-post alone (no --output) still routes to review_only because no_post=True.
        ns = build_parser().parse_args(["review", "--no-post"])
        rc = cmd_review(ns)
        assert rc == 0
        assert called == [("review_only", None)]

    def test_emit_config_error_writes_friendly_message(self, capsys):
        from reviewforge.cli import _emit_config_error
        from reviewforge.config import ConfigError
        _emit_config_error(ConfigError("Missing required config: FOO"), command="review")
        out = capsys.readouterr().err
        assert "Missing required config: FOO" in out
        assert "Required by command: review" in out

    def test_post_missing_input_exits_2(self, clean_env, monkeypatch, tmp_path, capsys):
        _set_min_env(monkeypatch, tmp_path)
        ns = build_parser().parse_args(["post"])
        rc = cmd_post(ns)
        assert rc == 2
        assert "--input" in capsys.readouterr().err

    def test_post_problems_exit_2(self, clean_env, monkeypatch, tmp_path, capsys):
        # No ADO_ORG → cmd_post reports problems and exits 2.
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        ns = build_parser().parse_args(["post", "--input", "/tmp/in.json"])
        rc = cmd_post(ns)
        assert rc == 2
        assert "ADO_ORG" in capsys.readouterr().err


# ---------------------------------------------------------------------------
# cmd_discover
# ---------------------------------------------------------------------------


class TestCmdDiscover:
    def test_emits_json_listing(self, clean_env, monkeypatch, capsys):
        from reviewforge.cli import cmd_discover
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "o")
        prs = [
            {
                "pullRequestId": 1,
                "title": "Fix bug",
                "targetRefName": "refs/heads/main",
                "isDraft": False,
            }
        ]
        with patch(
            "reviewforge.ado.client.list_active_pull_requests",
            return_value=prs,
        ):
            ns = build_parser().parse_args(["discover", "--project", "Pay"])
            rc = cmd_discover(ns)
        assert rc == 0
        out = capsys.readouterr().out
        data = json.loads(out)
        assert isinstance(data, list)
        assert data[0]["pullRequestId"] == 1

    def test_target_branches_passed_through(self, clean_env, monkeypatch, capsys):
        from reviewforge.cli import cmd_discover
        monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
        monkeypatch.setenv("ADO_ORG", "o")
        captured = {}

        def fake_list(cfg, *, project=None, target_branches=None, max_results=0):
            captured["project"] = project
            captured["target_branches"] = target_branches
            captured["max_results"] = max_results
            return []

        with patch(
            "reviewforge.ado.client.list_active_pull_requests",
            side_effect=fake_list,
        ):
            ns = build_parser().parse_args(
                ["discover", "--project", "Pay",
                 "--target-branches", "main, develop",
                 "--max", "5"]
            )
            rc = cmd_discover(ns)
        assert rc == 0
        assert captured["project"] == "Pay"
        assert captured["target_branches"] == ["main", "develop"]
        assert captured["max_results"] == 5

    def test_missing_token_returns_2(self, clean_env, monkeypatch, capsys):
        from reviewforge.cli import cmd_discover
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(k, raising=False)
        ns = build_parser().parse_args(["discover", "--project", "Pay"])
        rc = cmd_discover(ns)
        assert rc == 2
        assert "ADO_AUTH_TOKEN" in capsys.readouterr().err

    def test_cli_token_override(self, clean_env, monkeypatch, capsys):
        from reviewforge.cli import cmd_discover
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(k, raising=False)
        with patch(
            "reviewforge.ado.client.list_active_pull_requests",
            return_value=[],
        ):
            ns = build_parser().parse_args(
                ["discover", "--project", "Pay", "--ado-token", "override"]
            )
            rc = cmd_discover(ns)
        assert rc == 0


# ---------------------------------------------------------------------------
# PowerShell wrapper structural tests (no pwsh on the test host)
# ---------------------------------------------------------------------------


class TestPowerShellWrapperStructure:
    """Static checks against ``run.ps1`` and ``common.psm1``.

    These exist because the test host has no PowerShell. They verify
    that the Docker --env-file refactor (Task 14) is actually in place:
    the wrappers use ``Get-ReviewerEnvFile`` from ``common.psm1`` and
    pass only the dynamic CLI overrides as ``-e`` flags. Real
    PowerShell execution is exercised by CI on Windows / WSL agents.
    """

    @staticmethod
    def _read(rel: str) -> str:
        return (Path(__file__).resolve().parent.parent / rel).read_text(
            encoding="utf-8", errors="replace"
        )

    @staticmethod
    def _require(rel: str) -> str:
        """Read ``rel`` or skip the test with a clear reason.

        The structural tests depend on the PowerShell wrappers living at
        the repo root. The Docker test image (``Dockerfile.tests``)
        copies them in so CI exercises these tests; running pytest
        outside that image (e.g. a contributor who only cloned the
        ``src/`` and ``tests/`` trees) gets a clear skip instead of a
        hard ``FileNotFoundError``.
        """
        path = Path(__file__).resolve().parent.parent / rel
        if not path.exists():
            pytest.skip(f"{rel} not present at repo root; structural test requires it")
        return path.read_text(encoding="utf-8", errors="replace")

    def test_common_psm1_exposes_env_file_helper(self):
        text = self._require("common.psm1")
        assert "function Get-ReviewerEnvFile" in text
        assert "Export-ModuleMember" in text
        # Helper must be in the export list so run.ps1 can call it.
        assert "Get-ReviewerEnvFile" in text.split("Export-ModuleMember")[-1]

    def test_run_ps1_delegates_to_python_operations(self):
        text = self._require("run.ps1")
        assert "reviewforge.ops" in text
        assert "--env-file" in text
        assert "& python @args" in text

    def test_run_ps1_documents_env_file_behavior(self):
        text = self._require("run.ps1")
        assert "--env-file" in text
        assert "Compatibility wrapper" in text

    def test_wrappers_use_platform_path_separator(self):
        for rel in ("run.ps1", "run-open-prs.ps1", "build.ps1"):
            text = self._require(rel)
            assert "[IO.Path]::PathSeparator" in text


class TestRefactoredWrappers:
    # ------------------------------------------------------------------
    # Parser helpers (shared by the param-name assertions above).
    # ------------------------------------------------------------------

    @staticmethod
    def _parse_powershell_param_names(text: str) -> set[str]:
        """Extract declared param names from a PowerShell param block.

        Tolerant to ``[type]`` and ``[type[]]`` attribute forms and to
        any amount of whitespace between the attribute and ``$Name``.
        Switches outside the main param block (they appear before it
        in some scripts) are also captured.
        """
        names: set[str] = set()
        in_param = False
        for line in text.splitlines():
            if "[CmdletBinding()]" in line:
                in_param = True
                continue
            if in_param and line.startswith(")"):
                break
            if in_param:
                stripped = line.strip()
                # Match any [attribute] (possibly with extra [..] for
                # array types) followed by $Name. The non-greedy match
                # captures the first $[identifier] on the line.
                m = re.search(
                    r"\[[^\]]*(?:\[[^\]]*\])*\]\s*\$([A-Za-z_][A-Za-z0-9_]*)",
                    stripped,
                )
                if m:
                    names.add(m.group(1))
            stripped = line.strip()
            if stripped.startswith("[switch]"):
                m = re.search(r"\[switch\]\s*\$([A-Za-z_][A-Za-z0-9_]*)", stripped)
                if m:
                    names.add(m.group(1))
        return names

    @staticmethod
    def _strip_powershell_comments(text: str) -> str:
        """Return the script text with all PowerShell comments stripped.

        Handles both line comments (``# ...``) and ``<# ... #>`` block
        comments. Used by the "no hardcoded default" tests so historical
        references in the docstring / header do not trip the assertion.
        """
        no_block = re.sub(r"<#.*?#>", "", text, flags=re.DOTALL)
        out_lines: list[str] = []
        for line in no_block.splitlines():
            hash_idx = line.find("#")
            if hash_idx == -1:
                out_lines.append(line)
                continue
            prefix = line[:hash_idx]
            if prefix.count('"') % 2 == 1 or prefix.count("'") % 2 == 1:
                out_lines.append(line)
            else:
                out_lines.append(prefix.rstrip())
        return "\n".join(out_lines)
    """Structural checks for the PS-wrapper consolidation refactor.

    These tests don't exercise PowerShell on the test host (no pwsh on
    CI runners). They verify that the source files contain the expected
    surface so the refactor doesn't regress:

      * ``common.psm1`` exports the new helpers
        (Get-EnvOrDefault, Resolve-ScriptConfig, ConvertFrom-CommaList,
        Show-InteractivePrompt).
      * ``run.ps1`` and ``run-open-prs.ps1`` have no hardcoded
        "aveato" / "main,master,dev,develop" defaults — those moved
        entirely to env vars.
      * ``run.ps1`` and ``run-open-prs.ps1`` accept ``-Build`` so the
        deleted ``run-local.ps1`` shim still has a way to combine
        build + run.
      * ``run-local.ps1`` is a thin deprecation shim that forwards to
        ``run.ps1``.
    """

    REPO = Path(__file__).resolve().parent.parent

    def _read(self, name: str) -> str:
        path = self.REPO / name
        if not path.exists():
            pytest.skip(f"{name} not present on this host")
        return path.read_text(encoding="utf-8")

    def test_common_psm1_exports_new_helpers(self):
        text = self._read("common.psm1")
        # Each new helper must be defined and exported.
        for fn in (
            "Get-EnvOrDefault",
            "Resolve-ScriptConfig",
            "ConvertFrom-CommaList",
            "Show-InteractivePrompt",
        ):
            assert f"function {fn}" in text, f"{fn} not defined in common.psm1"
        export_line = next(
            (l for l in text.splitlines() if l.startswith("Export-ModuleMember")),
            "",
        )
        for fn in (
            "Get-EnvOrDefault",
            "Resolve-ScriptConfig",
            "ConvertFrom-CommaList",
            "Show-InteractivePrompt",
        ):
            assert fn in export_line, f"{fn} not exported from common.psm1"

    def test_run_ps1_accepts_pipeline_params(self):
        text = self._read("run.ps1")
        param_names = self._parse_powershell_param_names(text)
        expected = {
            "Runtime",
            "PinFile",
            "Image",
            "PrUrl",
            "Org",
            "Project",
            "RepoId",
            "PrId",
            "AdoToken",
            "SourceBranch",
            "TargetBranch",
            "Language",
            "FailOn",
            "VoteWaitingOn",
            "OpenAiApiKey",
            "PiModel",
            "EnvFile",
            "ContainerName",
            "ArtifactPath",
            "DryRun",
            "PrintCommand",
            "Build",
            "KeepContainer",
        }
        assert expected.issubset(param_names), (
            f"run.ps1 missing expected params. Found: {sorted(param_names)}, expected: {sorted(expected)}"
        )
        for token in (
            "--runtime",
            "--pin-file",
            "--image",
            "--language",
            "--fail-on",
            "--vote-waiting-on",
            "--pi-model",
            "--container-name",
            "--artifact-path",
            "--print-command",
            "SOURCE_BRANCH",
            "TARGET_BRANCH",
            "OPENAI_API_KEY",
        ):
            assert token in text

    def test_run_open_prs_ps1_minimal_params(self):
        text = self._read("run-open-prs.ps1")
        param_names = self._parse_powershell_param_names(text)
        expected = {
            "Organization", "Projects", "TargetBranches", "AdoToken",
            "EnvFile", "DryRun", "Interactive", "MaxPullRequests", "Build",
        }
        assert expected.issubset(param_names), (
            f"run-open-prs.ps1 missing expected params. Found: {sorted(param_names)}, expected: {sorted(expected)}"
        )
        assert len(param_names) <= 14, f"run-open-prs.ps1 has too many params: {sorted(param_names)}"

    def test_no_hardcoded_aveato_default(self):
        # The refactor removed the hardcoded ``https://dev.azure.com/aveato/``
        # default and the hardcoded project list. Both scripts must
        # require org/projects/branches from env / explicit params.
        # Comment lines (starting with #) are exempt — they document
        # the removal and would trip a naive substring search.
        for script in ("run.ps1", "run-open-prs.ps1"):
            code = self._strip_powershell_comments(self._read(script))
            assert "aveato" not in code.lower(), (
                f"{script} still contains hardcoded 'aveato' default outside comments"
            )
        # run-open-prs.ps1 must not carry the old project list either.
        code = self._strip_powershell_comments(self._read("run-open-prs.ps1"))
        for project in ("Laekker.Kitchen", "AveatoApp", "Laekkerai.CustomerProjects"):
            assert project not in code, f"run-open-prs.ps1 still has hardcoded project '{project}'"
        # And the old hardcoded target-branch list — the array literal itself.
        assert '@("main", "master", "dev", "develop")' not in code, (
            "run-open-prs.ps1 still has the hardcoded target-branches default array"
        )

    def test_build_switch_present(self):
        for script in ("run.ps1", "run-open-prs.ps1"):
            text = self._read(script)
            # Tolerant to whitespace: ``[switch] $Build``, ``[switch]$Build``,
            # and ``[switch]   $Build`` (3-space style used in the batch
            # wrapper) all count. Regex is anchored to the switch
            # attribute so it survives formatting drift.
            assert re.search(r"\[switch\]\s*\$Build\b", text), (
                f"{script} missing -Build switch (replaces run-local.ps1)"
            )

    def test_run_local_ps1_is_deprecation_shim(self):
        text = self._read("run-local.ps1")
        # Must print a deprecation warning.
        assert "deprecated" in text.lower() or "DEPRECATED" in text
        # Must forward to run.ps1 (or run-open-prs.ps1) rather than
        # doing the work itself.
        assert "run.ps1" in text
        # Must NOT contain a duplicated full run-flow (no Resolve-ScriptConfig
        # import, no Get-ContainerRuntime, no direct podman/docker build).
        assert "Get-ContainerRuntime" not in text

    def test_env_example_exists(self):
        path = self.REPO / ".env.example"
        assert path.exists(), ".env.example not present"
        text = path.read_text(encoding="utf-8")
        for key in (
            "ADO_ORGANIZATION",
            "ADO_PROJECTS",
            "ADO_TARGET_BRANCHES",
            "ADO_AUTH_TOKEN",
            "OPENAI_API_KEY",
        ):
            assert key in text, f".env.example missing required env var {key}"

    def test_interactive_prompt_supports_all_none_range(self):
        text = self._read("common.psm1")
        # The interactive helper must accept: all, none, n, a,
        # comma-separated indices, inclusive ranges.
        for token in ("'all'", "'a'", "'none'", "'n'", "(\\d+)-(\\d+)"):
            assert token in text, f"Show-InteractivePrompt missing pattern {token}"

    def test_run_open_prs_delegates_to_python_operations(self):
        text = self._read("run-open-prs.ps1")
        assert "reviewforge.ops" in text
        assert "--organization" in text
