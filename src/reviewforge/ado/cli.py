"""Legacy helper CLI shim for external callers."""
from __future__ import annotations

import sys
from . import operations as _operations

if __name__ == "__main__":
    raise SystemExit(_operations.main())

sys.modules[__name__] = _operations
