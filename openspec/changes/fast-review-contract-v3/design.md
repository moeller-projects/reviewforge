# Design

## Verified runtime facts

- `PiCliRunner` augments prompt files with only the language directive
  (`ai/runner.py:_resolve_system_prompt` → `ai/prompts.augment_prompt_file`).
  `BuildArtifactsStage` composes standards but is not in the production
  pipeline, so the updated standards never reach `single_pi`.
- `ReviewResult` and `ChunkResult` used `extra="ignore"`, silently discarding
  `work_type`, `biggest_unknown`, `test_gaps`, and `escalation_hints`.
- Chunk merging and synthesis handled only `findings` and `uncertainties`.

## Prompt composition

`ai.prompts._compose` centralizes prompt body + optional standards + language
directive. `PiCliRunner._is_review_prompt` appends standards only for the fast
review prompt and the legacy review prompt; synthesis, comment replies, and AC
coverage prompts remain standards-free.

## Schema contract

- `PrSummary` gains `work_type` (validated enum, default `mixed` for
  programmatic construction) and `biggest_unknown`.
- `TestGap`, `EscalationHint`, and the `WorkType`, `Classification`,
  `SuggestedFocus`, and `Danger` literals are added.
- `RichEvidence` gains `threads` and validates classification, positive
  changed lines, and the prior-thread/thread-id requirement.
- `RichFinding` validates repo-relative files, positive lines, regression
  evidence, work-item anchoring, and severity.
- `Uncertainty.reason` is required and cross-chunk entries require low
  confidence.
- `ReviewResult` and `ChunkResult` carry `test_gaps`, `escalation_hints`, and
  (chunk) `discarded_findings`, with caps at 5, 3, and 3.

## Chunk merging and synthesis

Each chunk may return `findings`, `test_gaps`, `uncertainties`,
`escalation_hints`, and `discarded_findings`. Findings dedupe by
file/line/title; gaps dedupe by file/behavior and cap at 5; hints dedupe by
files/reason, order critical first, and cap at 3; uncertainties dedupe by
topic/reason. Synthesis produces full framing including `work_type`,
`biggest_unknown`, and `intent`; a deterministic fallback supplies conservative
values when synthesis fails.

## Deterministic normalization

After the primary (and optional escalation) pass, `_normalize_review`:

- sets `architectural_impact` to `no significant architectural impact` when no
  architecture graph facts are present;
- derives `review_confidence.level` from the lowest finding confidence and
  downgrades for unresolved `biggest_unknown` or cross-chunk uncertainties;
- mirrors `metrics.confidence` to the derived level and raises
  `testsRead`/`symbolsInspected`/`workItemsRead` to evidence-derived lower
  bounds.

## Escalation policy

Escalation hints are always persisted in `review-result.json` and counted in
stage details; they are never posted as ADO comments. When
`ESCALATION_REVIEW_ENABLED=1`, a focused `ReviewResult` pass reviews the union
of hinted files (with `ESCALATION_REVIEW_MODEL` overriding `PI_MODEL` when
set). Focused findings replace revisited findings and add new ones; failures
retain the primary review.

## Boundaries

`review_result_to_final_doc` remains findings-only. `review_result_to_sarif`
adds `testGaps` and `escalationHints` run-property counts. Projection and the
default comment formatter surface `evidence.threads` for prior-thread findings.
