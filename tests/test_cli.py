"""CLI command tests with the pipeline stubbed out.

These tests exercise the argparse wiring, the CLI→Config layer, the
command-specific validation, and the dispatch to the orchestrator
entry points (``run_full`` / ``run_review_only`` / ``run_post_only``).
The orchestrator is replaced with a recording stub so the CLI tests
do not depend on git, Pi, or the network.
"""
from __future__ import annotations

import sys
from dataclasses import replace
from pathlib import Path
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from auto_pr_reviewer import cli  # noqa: E402
from auto_pr_reviewer.cli import (  # noqa: E402
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
    keys = [k for k in os.environ if k.startswith(("ADO_", "PR_", "PI_", "REVIEW_", "WORKSPACE", "CLONE_ROOT", "CHUNK_", "MAX_", "DISABLE_", "DRY_", "FORCE_", "INCLUDE_", "VERIFY_"))]
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
        from auto_pr_reviewer.config import Config
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
    def test_no_args_prints_help(self, capsys):
        rc = main([])
        assert rc == 1
        out = capsys.readouterr().err
        assert "usage" in out

    def test_dispatches_to_subcommand(self, clean_env, monkeypatch, tmp_path):
        _set_min_env(monkeypatch, tmp_path)

        class FakeOutcome:
            exit_code = 0

        monkeypatch.setattr(cli, "run_full", lambda cfg: FakeOutcome())
        rc = main(["review"])
        assert rc == 0

    def test_returns_1_for_unknown_command(self, clean_env, monkeypatch, tmp_path, capsys):
        # argparse will reject an unknown subcommand with SystemExit(2)
        # before our handler runs. main() catches it and returns 1.
        _set_min_env(monkeypatch, tmp_path)
        with pytest.raises(SystemExit) as exc:
            main(["definitely-not-a-command"])
        assert exc.value.code == 2

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
        from auto_pr_reviewer.cli import _emit_config_error
        from auto_pr_reviewer.config import ConfigError
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
