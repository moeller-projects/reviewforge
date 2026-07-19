# Plan: Reasoning Engine and Rich Review Output

## Goal
Refactor the Pi-driven portion of the review pipeline behind a single abstraction, the **Reasoning Engine**, and enrich the review result with diagnostics, metrics, uncertainty, evidence, and summary information. The pipeline collapses from many stage-oriented Pi calls to:

```
Fetch metadata → Prepare repo → Collect context → ReasoningEngine → Post
```

The engine itself decides whether to reason in one call, multiple calls, or via a different model/implementation. Later, `SinglePiReasoningEngine`, `MultiStageReasoningEngine`, `ClaudeReasoningEngine`, or `GPTReasoningEngine` can be swapped without touching orchestration.

This plan supersedes and generalizes [`fast-review-mode.md`](./fast-review-mode.md): the single-call fast mode becomes one implementation of the `ReasoningEngine` abstraction.

## Scope

### In scope
1. Introduce a `ReasoningEngine` abstraction with `execute(context) -> ReviewResult`.
2. Define a unified `ReviewResult` schema that includes findings, summaries, diagnostics, and metrics.
3. Refactor the existing multi-stage Pi logic into `MultiStageReasoningEngine` (current behavior preserved).
4. Add `SinglePiReasoningEngine` using the model's internal scratchpad.
5. Update the pipeline orchestrator to use the new abstraction.
6. Update posting and summary generation to consume `ReviewResult`.
7. Update the comment formatter to handle observation/impact/recommendation and rich evidence.
8. Tests and documentation.

### Out of scope
- Removing the default pipeline.
- Changing ADO posting contracts (markers, dedupe keys, comment format shape).
- Adding non-Pi reasoning engines (Claude, GPT) — the interface supports them, but concrete implementations are future work.
- Changing how ADO metadata is fetched or how repos are prepared.

## Risk tier
**Critical** — changes the core execution model, the result schema, and the orchestrator. It is additive, but touches every output of the review.

## Proposed schema

### ReviewResult

```json
{
  "pr_summary": {
    "intent": "string",
    "implementation_summary": "string",
    "architectural_impact": "string",
    "risk_assessment": "string",
    "positive_observations": ["string"]
  },
  "findings": [
    {
      "title": "string",
      "observation": "string",
      "impact": "string",
      "recommendation": "string",
      "severity": "blocker|major|minor|nit",
      "file": "string|null",
      "line": "integer|null",
      "contextBasis": "diff-only|surrounding-code-read|full-module-review|null",
      "evidence": {
        "changedLines": [1, 2],
        "relatedFiles": ["src/foo.py"],
        "testsRead": ["tests/foo_test.py"],
        "workItems": ["12345"],
        "whyNewInThisPr": "string",
        "whyNotIntentional": "string"
      }
    }
  ],
  "discarded_findings": [
    {"reason": "string", "count": 0}
  ],
  "good_practices": ["string"],
  "uncertainties": ["string"],
  "metrics": {
    "changedFilesReviewed": 0,
    "filesIgnored": 0,
    "testsRead": 0,
    "symbolsInspected": 0,
    "confidence": "high",
    "reviewDepth": "deep"
  },
  "review_confidence": {
    "level": "high",
    "reasons": ["string"]
  }
}
```

### Pydantic schemas

- `ReviewResult` — top-level container.
- `PrSummary` — intent, implementation, impact, risk, positives.
- `RichFinding` — observation, impact, recommendation, evidence. No per-finding confidence; review-wide confidence lives in `ReviewResult.review_confidence`.
- `Evidence` — changed lines, related files, tests, work items, reasoning.
- `DiscardedFinding` — reason + count.
- `ReviewMetrics` — counts and qualitative assessment.
- `ReviewConfidence` — review-wide confidence level + reasons.
- Reuse existing `Severity`, `Confidence`, `ContextBasis` literals.

## Reasoning Engine abstraction

```python
class ReasoningEngine(ABC):
    @abstractmethod
    def execute(self, ctx: StageContext) -> ReviewResult:
        """Run the reasoning loop and return a structured review result."""
        ...

    @property
    @abstractmethod
    def name(self) -> str:
        ...
```

Implementations:

| Engine | Description |
|---|---|
| `MultiStageReasoningEngine` | Refactor of the existing stages: intent → plan → digest → review → verify → calibrate → acceptance-criteria coverage. Consumes the output of `CollectContextStage`. Writes intermediate stage artifacts for observability. |
| `SinglePiReasoningEngine` | One Pi call with an internal scratchpad. The agent reads and searches files on its own via Pi tools. Returns a `ReviewResult` directly. No intermediate JSON artifacts. |
| `ClaudeReasoningEngine` | Future. |
| `GPTReasoningEngine` | Future. |

## Pipeline changes

The pipeline becomes:

```python
def build_pipeline(cfg: Config) -> list[Stage]:
    stages: list[Stage] = [
        FetchPrMetadataStage(),
        PrepareRepositoryStage(),
    ]
    if cfg.reasoning_engine == "multi_stage":
        # Multi-stage engine consumes deterministic context.
        stages.append(CollectContextStage())
    stages.append(ExecuteReasoningEngineStage())
    stages.append(PostToAdoStage())
    return stages
```

`ExecuteReasoningEngineStage` selects the engine from `cfg.reasoning_engine` (default `single_pi`) and writes the canonical `ReviewResult` plus the legacy `final-findings.json` projection.

- `SinglePiReasoningEngine` is the production path. Python deterministically reduces oversized context and Pi performs one logical review invocation, with any JSON repair tracked separately.
- `MultiStageReasoningEngine` is an explicit fallback that retains intent → plan → collect → digest → review → verify → calibrate → acceptance-criteria coverage.

## Posting and summary behavior

| ReviewResult field | Destination |
|---|---|
| `findings` | Posted to ADO as threaded comments; also written to `final-findings.json`. |
| `pr_summary`, `metrics`, `review_confidence`, `discarded_findings`, `uncertainties`, `good_practices` | Written to `run-summary.json` and the dashboard artifacts; **not** posted to ADO. |

### Comment formatting

The formatter synthesizes a finding's `message` from `observation`, `impact`, and `recommendation` if the new fields are present, preserving the existing Markdown layout. For example:

```markdown
#### 🟠 major — {title}

**Observation:** {observation}

**Impact:** {impact}

**Recommendation:** {recommendation}

{evidence block}

<!-- prb:{key} -->
```

## Implementation phases

### Phase 1: Schema and interface

- Add `ReviewResult` Pydantic schemas to `src/reviewforge/pipeline/schemas.py`.
- Add `ReasoningEngine` ABC to `src/reviewforge/reasoning/engine.py`.
- Add `cfg.reasoning_engine: str` (default `"single_pi"`) and `cfg.fast_review: bool` (alias) to `Config` and CLI.
- Add tests for schema validation.

### Phase 2: Multi-stage reasoning engine (must be implemented first)

- Move the Pi-driven stage logic from `ReconstructIntentStage`, `PlanContextStage`, `ContextDigestStage`, `ReviewDiffStage`, `VerifyFindingsStage`, and `CalibrateSeverityStage` into `MultiStageReasoningEngine`.
- Move `AcceptanceCriteriaCoverageStage` logic into the engine so the engine returns a fully assembled `ReviewResult` including AC coverage findings.
- The engine consumes the output of `CollectContextStage` and returns a `ReviewResult` assembled from its internal results.
- Keep existing intermediate artifacts (intent, plan, digest, collected, candidate, verified, severity, final) so observability is preserved.
- Delete the old `pipeline/stages/*.py` modules once their logic is moved into the engine.
- **Behavior goal:** After Phase 2, the output of `run_full` is identical to the current pipeline.
- This phase must be completed and validated before `SinglePiReasoningEngine` is implemented.

### Phase 3: Single-call reasoning engine (after multi-stage is stable)

- Add `SinglePiReasoningEngine` that makes one Pi call with a scratchpad prompt.
- The agent reads and searches files on its own via Pi tools; `CollectContextStage` is skipped in this mode.
- Prompt: instruct the model to maintain an internal review notebook, do NOT emit intermediate JSON, and return only the final `ReviewResult`.
- When `cfg.reasoning_engine == "single_pi"`, the pipeline uses this engine.
- No intermediate stage artifacts are produced; only `review-result.json` and the synthesized `final-findings.json`.
- Add tests for the single-call path.

### Phase 4: Rich output integration

- Update the prompts (for both engines) to request the new fields: `pr_summary`, `metrics`, `review_confidence`, `discarded_findings`, `uncertainties`, `good_practices`, and rich `evidence`.
- For `MultiStageReasoningEngine`, populate fields that are not yet available with null/empty values; fill them incrementally as the engine evolves.
- Update `PostToAdoStage` to post only `findings` and ignore non-ADO fields.
- Update `RunSummary`/`artifacts/summary.py` to include the new diagnostic fields.
- Update the comment formatter to use observation/impact/recommendation when present.
- Add tests for rich output, metrics, and diagnostics.
- Update documentation.

## Implementation tasks

```text
tasks:
- id: RE-01
  title: Add ReviewResult Pydantic schemas
  estimate: S
  depends_on: []
  done_when: Schemas exist and tests validate correct and invalid JSON.

- id: RE-02
  title: Define ReasoningEngine abstraction
  estimate: S
  depends_on: []
  done_when: `src/reviewforge/reasoning/engine.py` contains the ABC, registry, and factory.

- id: RE-03
  title: Add reasoning_engine config + CLI flag
  estimate: S
  depends_on: [RE-02]
  done_when: Config supports `REASONING_ENGINE=multi_stage|single_pi`, CLI has `--reasoning-engine`, and every configuration constructor defaults to `single_pi`.

- id: RE-04
  title: Implement MultiStageReasoningEngine
  estimate: L
  depends_on: [RE-01, RE-02, RE-03]
  done_when: Existing Pi stages and AC coverage are encapsulated, old stage modules are deleted, the engine returns a `ReviewResult`, and output is byte-equivalent to the current pipeline.

- id: RE-05
  title: Refactor pipeline to ExecuteReasoningEngineStage
  estimate: M
  depends_on: [RE-04]
  done_when: `DEFAULT_PIPELINE` uses `ExecuteReasoningEngineStage` and all existing tests pass.

- id: RE-06
  title: Implement SinglePiReasoningEngine with scratchpad
  estimate: L
  depends_on: [RE-01, RE-02, RE-03]
  done_when: One Pi call produces a `ReviewResult` and the pipeline completes successfully with `REASONING_ENGINE=single_pi`.

- id: RE-07
  title: Update prompt for rich output fields
  estimate: M
  depends_on: [RE-06]
  done_when: Single-call prompt requests all rich fields; model returns them reliably in tests.

- id: RE-08
  title: Update comment formatter for observation/impact/recommendation
  estimate: M
  depends_on: [RE-01]
  done_when: Formatter renders rich findings and still renders legacy findings correctly.

- id: RE-09
  title: Update run summary and diagnostics output
  estimate: M
  depends_on: [RE-07]
  done_when: `run-summary.json` contains `pr_summary`, `metrics`, `review_confidence`, `discarded_findings`, `uncertainties`, and `good_practices`.

- id: RE-10
  title: Tests and documentation
  estimate: M
  depends_on: [RE-05, RE-06, RE-08, RE-09]
  done_when: Coverage gate passes, new tests cover both engines, and docs are updated.

dependencies:
- RE-01 → RE-04: engine returns ReviewResult
- RE-01 → RE-06: engine returns ReviewResult
- RE-02 → RE-04: engine implements interface
- RE-02 → RE-06: engine implements interface
- RE-03 → RE-04: config selects engine
- RE-03 → RE-06: config selects engine
- RE-04 → RE-05: pipeline uses the engine
- RE-05 → RE-10: full pipeline tests
- RE-06 → RE-07: single-call engine uses scratchpad
- RE-07 → RE-09: summary consumes rich fields
- RE-08 → RE-10: formatter tests
- RE-09 → RE-10: summary tests

critical_path:
- RE-01 → RE-04 → RE-05 → RE-10
- RE-02 → RE-04 → RE-05 → RE-10
- RE-06 → RE-07 → RE-09 → RE-10
```

## Risks

- **RISK-1** — Large refactor. Moving six stages into one engine changes the control flow. The multi-stage engine must be proven byte-equivalent before the single-call engine is trusted.
- **RISK-2** — Prompt reliability. Single-call mode must produce a valid `ReviewResult` every time. The scratchpad prompt will need iteration.
- **RISK-3** — Schema churn. The `ReviewResult` schema is a public contract for dashboards and downstream consumers. Changes after release are breaking.
- **RISK-4** — ADO compatibility. The comment formatter must continue producing the same marker line and overall layout so deduplication keeps working.
- **RISK-5** — Coverage gate. Both engines and the new schema need tests to keep coverage ≥ 95%.

## Decisions

Design decisions embedded in this plan:
- `SinglePiReasoningEngine` is the production default; `MultiStageReasoningEngine` is explicit fallback only.
- Python owns deterministic orchestration and context reduction; Pi owns the logical review.
- Intermediate compatibility artifacts remain available but are projections, not independent production reasoning stages.
- `ReviewResult` is written to `review-result.json` as a stable artifact.
- Per-finding `confidence` is removed; review-wide confidence lives in `ReviewResult.review_confidence`.
- Non-finding fields (metrics, summaries, positives, discarded, uncertainties) are diagnostic only and are not posted to ADO.
- `good_practices` stay in `run-summary.json` only; they are not posted to ADO.

## Next action

Begin with **RE-01** (ReviewResult schema) and **RE-02** (ReasoningEngine interface) in parallel. Once both are stable, implement **RE-04** (multi-stage engine) before **RE-06** (single-call engine) so there is a proven-behavior baseline to compare against.
