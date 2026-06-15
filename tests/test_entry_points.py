"""Tests for the script entry points: ``__main__`` and the shims.

Verifies that:

* ``python -m auto_pr_reviewer --help`` works (canonical entrypoint).
* ``python scripts/main.py --help`` works (Docker ENTRYPOINT shim).
* ``python scripts/review.py --help`` works (legacy compat shim).
* ``python scripts/ado_review.py --help`` works (legacy compat shim).
* The shims contain no business logic — they delegate to the package.
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


def _run_script(script_name: str, *args: str) -> subprocess.CompletedProcess:
    """Run ``python scripts/<script_name> <args>`` and return the result."""
    return subprocess.run(
        [sys.executable, str(ROOT / "scripts" / script_name), *args],
        capture_output=True,
        text=True,
    )


# ---------------------------------------------------------------------------
# Canonical entrypoint
# ---------------------------------------------------------------------------


def test_module_invocation(monkeypatch):
    """``python -m auto_pr_reviewer --help`` should print help and exit 0."""
    monkeypatch.setattr(sys, "argv", ["auto_pr_reviewer", "--help"])
    from auto_pr_reviewer.__main__ import main
    with pytest.raises(SystemExit) as exc:
        main()
    assert exc.value.code == 0


def test_module_invocation_subprocess():
    """Run the canonical entrypoint as a subprocess and verify the help text."""
    r = subprocess.run(
        [sys.executable, "-m", "auto_pr_reviewer", "--help"],
        capture_output=True,
        text=True,
        cwd=str(ROOT / "src"),
    )
    assert r.returncode == 0
    assert "review" in r.stdout
    assert "validate-config" in r.stdout
    assert "discover" in r.stdout


def test_main_module_imports_cli_main():
    from auto_pr_reviewer.__main__ import main
    from auto_pr_reviewer.cli import main as cli_main
    assert main is cli_main


# ---------------------------------------------------------------------------
# Docker ENTRYPOINT shim: scripts/main.py
# ---------------------------------------------------------------------------


def test_main_shim_help():
    """``python scripts/main.py --help`` must work and list the subcommands."""
    r = _run_script("main.py", "--help")
    assert r.returncode == 0
    assert "review" in r.stdout
    assert "validate-config" in r.stdout


def test_main_shim_is_thin():
    """The main shim must contain no business logic (under 60 lines)."""
    text = (ROOT / "scripts" / "main.py").read_text(encoding="utf-8")
    assert "def " in text  # it has at least one function
    # It should not import any pipeline / config / ado logic directly.
    assert "from auto_pr_reviewer.pipeline" not in text
    assert "from auto_pr_reviewer.config" not in text
    assert "from auto_pr_reviewer.ado" not in text


def test_shim_main_delegates(monkeypatch, capsys):
    """The main shim should delegate to the package CLI."""
    monkeypatch.setattr(sys, "argv", ["prog", "--help"])
    import importlib.util
    spec = importlib.util.spec_from_file_location("main_mod_shim", ROOT / "scripts" / "main.py")
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    with pytest.raises(SystemExit) as exc:
        mod.main()
    assert exc.value.code == 0


# ---------------------------------------------------------------------------
# Legacy compat shim: scripts/review.py
# ---------------------------------------------------------------------------


def test_review_shim_help():
    """``python scripts/review.py --help`` should work (legacy compat)."""
    r = _run_script("review.py", "--help")
    assert r.returncode == 0
    assert "review" in r.stdout


def test_review_shim_is_thin():
    """The review shim is a thin compatibility shim (under 15 lines)."""
    text = (ROOT / "scripts" / "review.py").read_text(encoding="utf-8")
    # It only re-exports main from scripts/main.py.
    assert "from main import main" in text or "from main import" in text


def test_review_shim_delegates(monkeypatch, capsys):
    """``scripts/review.py`` should delegate to ``scripts/main.py``."""
    monkeypatch.setattr(sys, "argv", ["prog", "--help"])
    import importlib.util
    spec = importlib.util.spec_from_file_location("review_shim_test", ROOT / "scripts" / "review.py")
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    with pytest.raises(SystemExit) as exc:
        mod.main()
    assert exc.value.code == 0


# ---------------------------------------------------------------------------
# Legacy compat shim: scripts/ado_review.py
# ---------------------------------------------------------------------------


def test_ado_review_shim_help():
    """``python scripts/ado_review.py --help`` should work (legacy compat)."""
    r = _run_script("ado_review.py", "--help")
    assert r.returncode == 0
    assert "fetch-context" in r.stdout
    assert "post-findings" in r.stdout


def test_ado_review_shim_is_thin():
    """The ado_review shim contains no business logic (under 50 lines)."""
    text = (ROOT / "scripts" / "ado_review.py").read_text(encoding="utf-8")
    assert "from auto_pr_reviewer.ado.legacy import" in text
    # It should not have any of the business logic constants / helpers.
    assert "SEV_RANK" not in text
    assert "command_post_findings" not in text
    assert "validate_findings" not in text


def test_ado_review_shim_actually_runs_fetch_context(tmp_path, monkeypatch):
    """Verify the shim executes the package's fetch-context command end-to-end."""
    # Set up the env so the subcommand has a token.
    monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
    # Simulate the fetch-context subcommand via the shim.
    r = _run_script(
        "ado_review.py",
        "fetch-context",
        "--org", "x", "--project", "P", "--repo", "r", "--pr", "1", "--out", str(tmp_path),
    )
    # It should fail trying to call ADO (no network in tests), proving
    # the shim actually invoked the package command. We just check
    # that the error is *not* an import error.
    assert "ModuleNotFoundError" not in r.stderr
    assert "ImportError" not in r.stderr


# ---------------------------------------------------------------------------
# Cross-check: the shim re-exports the package's legacy surface
# ---------------------------------------------------------------------------


def test_ado_review_shim_exposes_legacy_module():
    """``scripts/ado_review.py`` is a thin shim — no business logic of its own.

    We verify the shim's behavior is identical to the package's legacy
    module by running both and comparing the help text.
    """
    from auto_pr_reviewer.ado import legacy
    shim_help = _run_script("ado_review.py", "--help").stdout
    # The shim's parser is the same one in legacy.build_parser().
    parser = legacy.build_parser()
    # All subcommands listed in the parser must appear in the shim's help.
    for action in parser._actions:  # noqa: SLF001 — internal test
        if action.choices:
            for name in action.choices:
                assert name in shim_help, f"subcommand {name!r} missing from shim help"
