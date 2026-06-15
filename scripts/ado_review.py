#!/usr/bin/env python3
"""Thin compatibility shim for the original ``scripts/ado_review.py``.

All real implementation lives in
:mod:`auto_pr_reviewer.ado.legacy` (and the broader
:mod:`auto_pr_reviewer.ado` package). This script exists so that:

* the Docker container's existing ENTRYPOINT contract still works,
* older PowerShell wrappers that shell out to ``python
  scripts/ado_review.py fetch-context ...`` still work,
* the old test suite (``tests/test_ado_review.py``) that imports
  ``ado_review`` as a module name still works.

It must not contain any business logic. Add new behavior in
:mod:`auto_pr_reviewer.ado.legacy` (or a sibling submodule) instead.
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
    from auto_pr_reviewer.ado.legacy import main as legacy_main
    return int(legacy_main())


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
