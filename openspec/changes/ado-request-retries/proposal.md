## Why

Transient Azure DevOps failures currently abort a review after model work has completed.

## What Changes

- Add bounded, configurable ADO request retries with jitter and Retry-After support.
- Retry safe reads for transient status and transport failures; preserve POST/PUT ambiguity safety.

## Capabilities

### New Capabilities
- `ado-request-retries`: Safe bounded retries for transient Azure DevOps requests.

### Modified Capabilities
- None.

## Impact

- ADO client, configuration, environment reference, and client tests.
