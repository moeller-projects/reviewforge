"""Tests for the script entry points: ``__main__`` and the shims."""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


def test_module_invocation(monkeypatch):
    """``python -m auto_pr_reviewer --help`` should print help and exit 0."""
    monkeypatch.setattr(sys, "argv", ["auto_pr_reviewer", "--help"])
    from auto_pr_reviewer.__main__ import main
    with pytest.raises(SystemExit) as exc:
        main()
    # argparse exits 0 on --help.
    assert exc.value.code == 0


def test_main_module_imports_cli_main():
    from auto_pr_reviewer.__main__ import main
    from auto_pr_reviewer.cli import main as cli_main
    assert main is cli_main


def test_shim_main_delegates(monkeypatch, capsys):
    """``scripts/main.py`` should delegate to the package CLI."""
    monkeypatch.setattr(sys, "argv", ["prog", "--help"])
    # Clear cached modules so the shim re-imports.
    for name in list(sys.modules):
        if name in {"main_mod", "main"} and not name.startswith("auto_pr_reviewer"):
            sys.modules.pop(name, None)
    import importlib.util
    spec = importlib.util.spec_from_file_location("main_mod_shim", ROOT / "scripts" / "main.py")
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    with pytest.raises(SystemExit) as exc:
        mod.main()
    assert exc.value.code == 0


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
