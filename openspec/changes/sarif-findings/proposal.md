## Why

External dashboards and code-scanning tools need a standard rendering of ReviewForge findings. SARIF should be emitted as an additive, best-effort projection without changing canonical results or ADO posting.

## What Changes

- Add a stdlib-only SARIF 2.1.0 projection of `ReviewResult`.
- Write `sarif-findings.json` after `review-result.json` in review runs.
- Add the artifact to the stable artifact contract and documentation.
- Continue reviews when SARIF generation or writing fails.

## Capabilities

### New Capabilities
- `sarif-findings`: Render canonical review findings as SARIF 2.1.0.

### Modified Capabilities
- None.
