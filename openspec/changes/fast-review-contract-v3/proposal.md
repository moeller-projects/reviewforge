## Why

`prompts/fast-review-system.md` v3 and `standards/clean-code.md` introduced framing fields, test gaps, escalation hints, lens accounting, and stricter standards, but the runtime dropped the new fields and never delivered the standards file to Pi.

## What Changes

- Compose review prompts with coding standards and the language directive.
- Extend `ReviewResult` and `ChunkResult` with `test_gaps`, `escalation_hints`, `work_type`, `biggest_unknown`, and chunk `discarded_findings`.
- Validate caps, enums, work-item anchors, regression evidence, and review confidence.
- Merge all chunk sections deterministically and synthesize full framing.
- Record escalation hints and optionally run a focused deeper pass.

## Capabilities

### New Capabilities

- `fast-review-contract-v3`: complete runtime support for the updated fast-review and clean-code contracts.

### Modified Capabilities

- `chunked-single-pi-review`: chunk responses now include `test_gaps`, `escalation_hints`, and `discarded_findings`.
- `canonical-review-artifacts`: `review-result.json` gains `test_gaps` and `escalation_hints`.

## Impact

Schemas, prompt composition, single-pi reasoning, multi-stage fallback, posting/artifact boundaries, configuration, prompts, documentation, and tests. No new third-party dependencies.
