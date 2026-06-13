#!/usr/bin/env python3
"""Compatibility entrypoint. Prefer scripts/main.py."""
from main import main

if __name__ == "__main__":
    raise SystemExit(main())
