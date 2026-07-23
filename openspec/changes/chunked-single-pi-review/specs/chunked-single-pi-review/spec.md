## ADDED Requirements

### Requirement: Commit-aware single Pi review
Single-Pi review MUST include up to `COMMIT_CONTEXT_MAX` persisted commit subjects before the unified diff.

#### Scenario: Prepared run has commits
- **WHEN** the commits artifact contains more commit subjects than the configured cap
- **THEN** the instruction MUST contain the first capped subjects before the diff.

### Requirement: Coherent oversized diff review
The reviewer MUST partition an oversized Python-computed unified diff in stable `diff --git` order and MUST submit each chunk in the same Pi session without granting additional tools.

#### Scenario: Diff exceeds context budget
- **WHEN** the unified diff exceeds `MAX_DIFF_BYTES`
- **THEN** the runtime MUST issue ordered chunk review calls and preserve the `--tools read,grep` posture.

### Requirement: Deterministic partial-result merge
The reviewer MUST validate chunk output as partial findings and uncertainties, deduplicate findings by file, line, and normalized title while retaining the first, and validate one synthesized `ReviewResult`.

#### Scenario: Chunks repeat a finding
- **WHEN** two chunk results contain the same file, line, and normalized title
- **THEN** the final result MUST contain only the first finding.
