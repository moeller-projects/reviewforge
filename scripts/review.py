#!/usr/bin/env python3
"""Compatibility shim for the original ``scripts/review.py``.

The new package owns the CLI; this file remains so that older wrappers
calling ``python scripts/review.py`` keep working.
"""
from main import main

if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
