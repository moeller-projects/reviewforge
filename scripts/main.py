#!/usr/bin/env python3
from __future__ import annotations

from config import Config
from pipeline.orchestrator import run


def main() -> int:
    cfg = Config.from_env()
    cfg.validate_files()
    return run(cfg)


if __name__ == "__main__":
    raise SystemExit(main())
