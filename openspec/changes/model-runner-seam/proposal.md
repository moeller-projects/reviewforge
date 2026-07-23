## Why

Pi-specific CLI mechanics are currently embedded in the only model runner, preventing a second backend without changing stages and engines.

## What Changes

- Define a narrow model execution protocol and Pi implementation name.
- Add a configured factory with only the Pi backend.
- Preserve Pi compatibility through a temporary alias.

## Capabilities

### New Capabilities
- `model-runner-seam`: Explicit model execution boundary with a single Pi backend.

### Modified Capabilities
- None.

## Impact

- AI runner, configuration, orchestrator construction, architecture docs, and tests.
