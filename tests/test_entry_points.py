"""Tests for the package entrypoints."""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


def test_module_invocation(monkeypatch):
    monkeypatch.setattr(sys, "argv", ["reviewforge", "--help"])
    from reviewforge.__main__ import main

    with pytest.raises(SystemExit) as exc:
        main()
    assert exc.value.code == 0


def test_module_invocation_subprocess():
    result = subprocess.run(
        [sys.executable, "-m", "reviewforge", "--help"],
        capture_output=True,
        text=True,
        cwd=str(ROOT / "src"),
    )
    assert result.returncode == 0
    assert {"review", "validate-config", "discover"} <= set(result.stdout.split())


def test_main_module_imports_cli_main():
    from reviewforge.__main__ import main
    from reviewforge.cli import main as cli_main

    assert main is cli_main


def test_cli_module_help():
    result = subprocess.run(
        [sys.executable, "-W", "default", "-m", "reviewforge.ado.cli", "--help"],
        capture_output=True,
        text=True,
        cwd=str(ROOT / "src"),
    )
    assert result.returncode == 0
    assert "fetch-context" in result.stdout
    assert "post-findings" in result.stdout
    assert "DeprecationWarning" in result.stderr


def test_cli_module_runs_without_import_error(tmp_path, monkeypatch):
    monkeypatch.setenv("ADO_AUTH_TOKEN", "t")
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "reviewforge.ado.cli",
            "fetch-context",
            "--org",
            "x",
            "--project",
            "P",
            "--repo",
            "r",
            "--pr",
            "1",
            "--out",
            str(tmp_path),
        ],
        capture_output=True,
        text=True,
        cwd=str(ROOT / "src"),
    )
    assert "ModuleNotFoundError" not in result.stderr
    assert "ImportError" not in result.stderr
