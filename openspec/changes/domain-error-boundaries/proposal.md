## Why

Library `SystemExit` raises prevent embedding ReviewForge and make error handling inconsistent across Git, Pi, ADO, and validation paths.

## What Changes

- Replace library `SystemExit` raises with structured domain exceptions carrying details.
- Translate domain exceptions to existing operator-facing stderr text and exit code 1 at CLI boundaries.
- Preserve stage failure records and artifact shapes.

## Capabilities

### New Capabilities
- `domain-error-boundaries`: Embeddable library error handling with stable CLI translation.

### Modified Capabilities
- None.

## Impact

- Git, Pi, ADO, validation, orchestration, and CLI error paths.
