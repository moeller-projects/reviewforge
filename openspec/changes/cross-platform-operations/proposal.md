## Why

Container build and review orchestration currently requires PowerShell and repeats version pins across independent CI and build files.

## What Changes

- Add checked-in version pins and make builds consume them explicitly.
- Add Python build, single-review, and batch-review container entrypoints.
- Keep PowerShell entrypoints as compatibility wrappers.
- Add CI enforcement for Azure pin agreement.

## Capabilities

### New Capabilities
- `cross-platform-operations`: Platform-neutral container operations and shared version pins.

### Modified Capabilities
- None.

## Impact

- Docker build arguments, Azure Pipelines, GitHub Actions, root PowerShell wrappers, tests, and operator documentation.
