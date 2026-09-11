## 1. Contract and prompt composition

- [x] 1.1 Centralize prompt composition with optional standards.
- [x] 1.2 Append standards and language to review prompts only.
- [x] 1.3 Update fast-review and chunk-synthesis prompt files.

## 2. Schemas and validation

- [x] 2.1 Add work-type, test-gap, escalation-hint, and classification literals.
- [x] 2.2 Extend `PrSummary`, `RichEvidence`, `RichFinding`, `Uncertainty`, `ChunkResult`, and `ReviewResult`.
- [x] 2.3 Enforce caps, enums, work-item anchoring, regression evidence, and repo-relative paths.

## 3. Reasoning runtime

- [x] 3.1 Merge all chunk sections deterministically.
- [x] 3.2 Synthesize full framing and deterministic fallback.
- [x] 3.3 Normalize review confidence, metrics, and architectural impact.
- [x] 3.4 Implement escalation persistence and optional focused pass.
- [x] 3.5 Populate the multi-stage fallback framing.

## 4. Boundaries and observability

- [x] 4.1 Keep final-findings and ADO comments findings-only.
- [x] 4.2 Add test-gap and escalation counts to stage details and SARIF properties.
- [x] 4.3 Surface prior-thread evidence in projection and comment formatting.

## 5. Tests and docs

- [x] 5.1 Add schema, composition, chunk-merge, normalization, and escalation tests.
- [x] 5.2 Update prompt, schema, metrics, and architecture docs.
- [x] 5.3 Run focused tests, full suite, and OpenSpec validation.
