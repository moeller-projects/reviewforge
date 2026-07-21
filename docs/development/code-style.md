# Code style

**Purpose:** define observed implementation conventions. **Audience:** contributors. **Mode:** reference.

The project uses Python type annotations, dataclasses for runtime state, Pydantic models for JSON contracts, and small modules grouped by boundary. Public modules declare `__all__` where appropriate. Errors use domain exceptions or explicit `SystemExit` at CLI/process boundaries.

Prefer existing standard-library facilities and repository helpers. Keep subprocess calls explicit, quote external identifiers, validate paths before reading, and never pass ADO credentials to Pi. Follow `standards/clean-code.md` when modifying review behavior.
