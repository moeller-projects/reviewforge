#!/usr/bin/env python3
"""Thin entrypoint that delegates to the :mod:`auto_pr_reviewer` package CLI.

This file is preserved for backward compatibility with existing PowerShell
wrappers (``run.ps1`` etc.) and the Docker ``ENTRYPOINT``. It must not contain
any business logic.
"""
from __future__ import annotations

import os
import sys


def _ensure_src_on_path() -> None:
    """Add ``src/`` to :data:`sys.path` if the package is not installed."""
    here = os.path.dirname(os.path.abspath(__file__))
    src = os.path.normpath(os.path.join(here, "..", "src"))
    if os.path.isdir(src) and src not in sys.path:
        sys.path.insert(0, src)


def main() -> int:
    _ensure_src_on_path()
    from auto_pr_reviewer.cli import main as cli_main
    return int(cli_main())


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
