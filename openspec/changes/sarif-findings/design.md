## Context

`ReviewResult` is the canonical domain output and `pipeline/projection.py` already isolates presentation formats. SARIF belongs beside that projection layer and must not alter schemas, prompts, or posting.

## Design

`pipeline/sarif.py` builds a minimal SARIF 2.1.0 dictionary using only the standard library. Finding titles become stable slug rule IDs; duplicate titles share one driver rule. Severity maps to SARIF levels, messages combine observation, impact, and recommendation, and locations are included only for file-and-line findings. Result properties carry confidence, context basis, evidence summary, and the existing `prb` dedupe identity. The execution stage writes the artifact immediately after the canonical review result and catches all emission errors, logging a warning.

## Risks

SARIF is observability output and could fail due to malformed model data or filesystem errors. The write is isolated in a best-effort block so canonical review and ADO posting remain authoritative.
