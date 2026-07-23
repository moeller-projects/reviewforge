## ADDED Requirements

### Requirement: Canonical review artifacts
The pipeline MUST operate on `ReviewResult` and its in-memory final-findings projection. It MUST publish `review-result.json` and a write-once `final-findings.json` projection, while post-only execution MUST post its supplied interchange document without creating fragment artifacts.

#### Scenario: Post existing final findings
- **WHEN** `reviewforge post --input` receives a valid final-findings document
- **THEN** posting MUST use that context-held document without reading severity or final fragment files

### Requirement: Optional multi-stage fragments
The multi-stage engine MUST retain intent, context, candidate, verification, and severity fragments only when `DEBUG_INTERMEDIATES=1`.

#### Scenario: Default multi-stage review
- **WHEN** multi-stage execution runs without `DEBUG_INTERMEDIATES`
- **THEN** fragment files MUST not remain in the run artifacts
