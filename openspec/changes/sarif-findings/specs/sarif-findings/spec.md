## ADDED Requirements

### Requirement: Canonical findings have a SARIF projection
ReviewForge MUST render `ReviewResult` findings as a valid SARIF 2.1.0 log with ReviewForge tool metadata, deduplicated title-derived rules, severity levels, messages, optional file locations, and stable finding properties.

#### Scenario: Located finding is rendered
- **WHEN** a finding has a file and line
- **THEN** the SARIF result MUST contain a forward-slash repository-relative artifact URI and a start line

#### Scenario: General finding is rendered
- **WHEN** a finding has no file or line
- **THEN** the SARIF result MUST remain valid without a location

### Requirement: SARIF output is additive and best effort
The review pipeline MUST write `sarif-findings.json` after `review-result.json` and MUST continue the review when SARIF projection or writing fails.

#### Scenario: SARIF emission succeeds
- **WHEN** reasoning produces a canonical result
- **THEN** the run artifacts MUST include `sarif-findings.json` without changing ADO posting artifacts

#### Scenario: SARIF emission fails
- **WHEN** projection or artifact writing raises an exception
- **THEN** the pipeline MUST log a warning and continue with the canonical result and existing posting path
