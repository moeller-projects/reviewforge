"""Allow ``python -m auto_pr_reviewer`` to invoke the CLI."""
from __future__ import annotations

import sys

from .cli import main

if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
