"""Legacy helper CLI shim for external callers.

Deprecated: this module and its ``sys.modules`` self-replacement are removed
in 0.4.0. Import :mod:`reviewforge.ado.operations` or use the primary
``reviewforge`` CLI instead.
"""
from __future__ import annotations

import sys
import warnings

from . import operations as _operations

if __name__ == "__main__":
    warnings.warn(
        "reviewforge.ado.cli is deprecated and will be removed in 0.4.0; "
        "use reviewforge.ado.operations or the primary `reviewforge` CLI",
        DeprecationWarning,
        stacklevel=2,
    )
    raise SystemExit(_operations.main())

sys.modules[__name__] = _operations
