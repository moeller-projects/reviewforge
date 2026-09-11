# Fast review contract v3

## ADDED Requirements

### Requirement: Standards delivered to review prompts
The single-pi and legacy review prompts MUST append the configured coding standards and the language directive, and MUST NOT append standards to synthesis, comment-reply, or AC-coverage prompts.

#### Scenario: Fast review prompt
- **WHEN** a `single_pi` review call runs
- **THEN** the `--append-system-prompt` file contains the fast review prompt, the coding standards, and the language directive.

#### Scenario: Synthesis prompt
- **WHEN** a chunk synthesis call runs
- **THEN** the `--append-system-prompt` file does not contain the coding standards.

### Requirement: Canonical contract fields are retained
`ReviewResult` MUST retain `pr_summary.work_type`, `pr_summary.biggest_unknown`, `test_gaps`, and `escalation_hints`, and `ChunkResult` MUST retain `test_gaps`, `escalation_hints`, and `discarded_findings`.

#### Scenario: Rich result round-trip
- **WHEN** a model returns a `ReviewResult` with framing, test gaps, and escalation hints
- **THEN** the validated result preserves every field with no loss.

#### Scenario: Invalid work type
- **WHEN** a supplied `ReviewResult` omits `pr_summary.work_type`
- **THEN** validation fails.

### Requirement: Contract limits and enums are enforced
`test_gaps` MUST be capped at 5, `escalation_hints` at 3, `good_practices` at 3, and `positive_observations` at 3. `work_type`, `suggested_focus`, and `danger` MUST use their declared literals.

#### Scenario: Over-cap test gaps
- **WHEN** a result contains more than 5 test gaps
- **THEN** validation fails.

### Requirement: Work-item and regression constraints
Work-item findings MUST have no file or line and severity major or blocker. Regression findings MUST cite changed lines. Prior-thread findings MUST cite a thread id.

#### Scenario: Anchored work-item finding
- **WHEN** a finding matches the work-item title prefix or classification but has a file or line
- **THEN** validation fails.

### Requirement: Chunked review merges every section
Chunked review MUST merge `findings`, `test_gaps`, `uncertainties`, `escalation_hints`, and `discarded_findings`, deduplicate by stable keys, and apply the contract caps.

#### Scenario: Repeated escalation hint
- **WHEN** two chunks emit the same escalation hint files and reason
- **THEN** the final result contains the hint once.

### Requirement: Synthesis produces full framing
Chunk synthesis MUST produce `pr_summary.intent` and `pr_summary.work_type`, and a failed synthesis MUST fall back to a valid `ReviewResult` with conservative framing.

#### Scenario: Synthesis failure
- **WHEN** the synthesis call fails or produces invalid JSON
- **THEN** the review completes with a `work_type` and a `biggest_unknown` that reflects the missing synthesis.

### Requirement: Deterministic confidence and architectural impact
The runtime MUST derive `review_confidence.level` from reported finding confidence and known missing context, MUST mirror `metrics.confidence`, and MUST emit exactly `no significant architectural impact` when architecture graph facts are absent.

#### Scenario: Missing architecture facts
- **WHEN** no architecture graph section is present
- **THEN** `pr_summary.architectural_impact` is exactly `no significant architectural impact`.

### Requirement: Escalation hints are advisory and optionally actionable
Escalation hints MUST persist in `review-result.json` and stage details, MUST NOT be posted as ADO comments, and MUST trigger a focused review pass only when escalation review is enabled.

#### Scenario: Escalation disabled
- **WHEN** a review produces escalation hints and escalation review is disabled
- **THEN** hints are recorded and no additional review call is made.

#### Scenario: Escalation enabled
- **WHEN** a review produces escalation hints and escalation review is enabled
- **THEN** a focused `ReviewResult` pass reviews the union of hinted files.
